import json
import logging
import os
import threading
import time

import pika
import uvicorn
from prometheus_client import Counter, Histogram, start_http_server

import inference
from app import app

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
logger = logging.getLogger("ai-sidecar.worker")

RABBITMQ_HOST = os.environ.get("RABBITMQ_HOST", "rabbitmq")
RABBITMQ_USER = os.environ.get("RABBITMQ_USER", "guest")
RABBITMQ_PASS = os.environ.get("RABBITMQ_PASS", "guest")

REQUEST_EXCHANGE = "ai-analysis-requested"
RESULT_EXCHANGE = "ai-analysis-completed"
QUEUE_NAME = "ai-analysis"

RECONNECT_DELAY_SECONDS = 5

ai_messages_total = Counter("ai_messages_total", "AI sidecar messages processed", ["status"])
ai_inference_seconds = Histogram("ai_inference_seconds", "AI sidecar inference duration in seconds")


def parse_request(body: bytes) -> dict:
    """MassTransit's standard envelope carries the payload under "message" (camelCase keys)."""
    envelope = json.loads(body)
    return envelope["message"]


def build_result(
    correlation_id: str,
    success: bool,
    tags: list[dict] | None = None,
    embedding: list[float] | None = None,
    origin_class: str | None = None,
    origin_confidence: float | None = None,
    error: str | None = None,
) -> dict:
    """Shape matches Contracts.Messages.AiAnalysisCompleted exactly (camelCase, plain JSON)."""
    return {
        "correlationId": correlation_id,
        "success": success,
        "tags": tags or [],
        "embedding": embedding or [],
        "originClass": origin_class,
        "originConfidence": origin_confidence,
        "error": error,
    }


def handle_message(body: bytes, analyze=inference.analyze) -> dict:
    """Never raises — any analyze() failure becomes a success:false result (poison-image safety)."""
    request = parse_request(body)
    correlation_id = request["correlationId"]

    try:
        with ai_inference_seconds.time():
            result = analyze(request["filePath"])
    except Exception as e:
        ai_messages_total.labels(status="error").inc()
        logger.warning("analyze failed for %s: %s", request.get("filePath"), e)
        return build_result(correlation_id, success=False, error=str(e))

    ai_messages_total.labels(status="ok").inc()
    return build_result(
        correlation_id,
        success=True,
        tags=result["tags"],
        embedding=result["embedding"],
        origin_class=result["originClass"],
        origin_confidence=result["originConfidence"],
    )


def _on_message(channel, method, _properties, body):
    result = handle_message(body)
    channel.basic_publish(
        exchange=RESULT_EXCHANGE,
        routing_key="",
        body=json.dumps(result),
        properties=pika.BasicProperties(content_type="application/json", delivery_mode=2),
    )
    # Always ack, even on inference failure — the result above already carries success:false;
    # nacking would just cause endless redelivery of a poison image.
    channel.basic_ack(delivery_tag=method.delivery_tag)


def _run_pika_loop() -> None:
    credentials = pika.PlainCredentials(RABBITMQ_USER, RABBITMQ_PASS)
    while True:
        try:
            connection = pika.BlockingConnection(
                pika.ConnectionParameters(host=RABBITMQ_HOST, credentials=credentials, heartbeat=600)
            )
            channel = connection.channel()
            channel.exchange_declare(exchange=REQUEST_EXCHANGE, exchange_type="fanout", durable=True)
            channel.exchange_declare(exchange=RESULT_EXCHANGE, exchange_type="fanout", durable=True)
            channel.queue_declare(queue=QUEUE_NAME, durable=True)
            channel.queue_bind(exchange=REQUEST_EXCHANGE, queue=QUEUE_NAME)
            channel.basic_qos(prefetch_count=1)
            channel.basic_consume(queue=QUEUE_NAME, on_message_callback=_on_message)
            logger.info("connected — consuming queue %s", QUEUE_NAME)
            channel.start_consuming()
        except Exception as e:
            logger.error("pika connection lost (%s) — reconnecting in %ss", e, RECONNECT_DELAY_SECONDS)
            time.sleep(RECONNECT_DELAY_SECONDS)


def main() -> None:
    inference.load_models()
    start_http_server(8001)
    api_thread = threading.Thread(
        target=lambda: uvicorn.run(app, host="0.0.0.0", port=8000, log_level="info"),
        daemon=True,
    )
    api_thread.start()
    _run_pika_loop()


if __name__ == "__main__":
    main()
