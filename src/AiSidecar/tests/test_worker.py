import json

import worker


def test_parse_request_extracts_message_payload():
    body = json.dumps({
        "messageId": "abc",
        "message": {"correlationId": "3f2a", "filePath": "/shared-temp/3f2a.jpg"},
    }).encode()

    request = worker.parse_request(body)

    assert request == {"correlationId": "3f2a", "filePath": "/shared-temp/3f2a.jpg"}


def test_build_result_success_shape():
    result = worker.build_result(
        "3f2a",
        success=True,
        tags=[{"tag": "dog", "confidence": 0.91, "model": "yolov8n"}],
        embedding=[0.1, 0.2],
        origin_class="photo",
        origin_confidence=0.93,
    )

    assert result == {
        "correlationId": "3f2a",
        "success": True,
        "tags": [{"tag": "dog", "confidence": 0.91, "model": "yolov8n"}],
        "embedding": [0.1, 0.2],
        "originClass": "photo",
        "originConfidence": 0.93,
        "error": None,
    }


def test_build_result_failure_defaults_empty_collections():
    result = worker.build_result("3f2a", success=False, error="boom")

    assert result["success"] is False
    assert result["tags"] == []
    assert result["embedding"] == []
    assert result["originClass"] is None
    assert result["originConfidence"] is None
    assert result["error"] == "boom"


def _envelope(correlation_id: str, file_path: str) -> bytes:
    return json.dumps({"message": {"correlationId": correlation_id, "filePath": file_path}}).encode()


def test_handle_message_success_path():
    def fake_analyze(file_path: str) -> dict:
        assert file_path == "/shared-temp/3f2a.jpg"
        return {
            "tags": [{"tag": "cat", "confidence": 0.8, "model": "yolov8n"}],
            "embedding": [0.5],
            "originClass": "photo",
            "originConfidence": 0.9,
        }

    result = worker.handle_message(_envelope("3f2a", "/shared-temp/3f2a.jpg"), analyze=fake_analyze)

    assert result["correlationId"] == "3f2a"
    assert result["success"] is True
    assert result["tags"] == [{"tag": "cat", "confidence": 0.8, "model": "yolov8n"}]
    assert result["error"] is None


def test_handle_message_error_path_never_raises():
    def failing_analyze(file_path: str) -> dict:
        raise RuntimeError("cannot open image")

    result = worker.handle_message(_envelope("3f2a", "/missing.jpg"), analyze=failing_analyze)

    assert result["correlationId"] == "3f2a"
    assert result["success"] is False
    assert result["tags"] == []
    assert result["embedding"] == []
    assert "cannot open image" in result["error"]
