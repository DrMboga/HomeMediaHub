"""Phase 12 Step 2 — truncated JPEGs must reach the models instead of failing the AI step.

Nine files failed the prod archive import this way (docs/backlog.md bucket 3): intact JFIF header,
no ff-d9 end-of-image marker, most likely partial messenger downloads. Ingest accepted them —
`MetadataService` reads headers only — so they have media rows and are visible in the gallery;
what they lacked was tags and an embedding. Pillow can decode them and refuses by policy, which
`ImageFile.LOAD_TRUNCATED_IMAGES` reverses.
"""

import io

import pytest

Image = pytest.importorskip("PIL.Image", reason="needs real Pillow, not a stub")
ImageFile = pytest.importorskip("PIL.ImageFile", reason="needs real Pillow, not a stub")

import inference  # noqa: E402  — imported after the Pillow guard on purpose


_SENTINEL = {"tags": [], "embedding": [0.5], "originClass": "photo", "originConfidence": 0.9}


def _truncated_jpeg(tmp_path, keep_fraction: float = 0.6):
    """A real JPEG with its tail — including the ff-d9 marker — chopped off."""
    buffer = io.BytesIO()
    # Noise, not a flat colour: a flat image compresses to almost nothing, and there would be no
    # scan data left to truncate.
    image = Image.new("RGB", (256, 256))
    image.putdata([(x * 7 % 256, y * 13 % 256, (x + y) % 256)
                   for y in range(256) for x in range(256)])
    image.save(buffer, format="JPEG", quality=95)

    data = buffer.getvalue()
    assert data.endswith(b"\xff\xd9"), "fixture is not a complete JPEG to begin with"

    path = tmp_path / "truncated.jpg"
    path.write_bytes(data[: int(len(data) * keep_fraction)])
    assert not path.read_bytes().endswith(b"\xff\xd9")
    return path


def test_load_truncated_images_policy_is_enabled_on_import():
    assert ImageFile.LOAD_TRUNCATED_IMAGES is True


def test_analyze_accepts_a_truncated_jpeg(tmp_path, monkeypatch):
    # The decode is what is under test; the models are not loaded in unit tests.
    monkeypatch.setattr(inference, "_run_inference", lambda image: _SENTINEL)

    result = inference.analyze(str(_truncated_jpeg(tmp_path)))

    assert result is _SENTINEL


def test_truncated_jpeg_would_fail_without_the_policy(tmp_path, monkeypatch):
    """Pins the fix as load-bearing: with the flag off, this is the exact prod failure."""
    monkeypatch.setattr(ImageFile, "LOAD_TRUNCATED_IMAGES", False)
    monkeypatch.setattr(inference, "_run_inference", lambda image: _SENTINEL)

    with pytest.raises(inference.InferenceError) as excinfo:
        inference.analyze(str(_truncated_jpeg(tmp_path)))

    assert "truncated" in str(excinfo.value).lower()


def test_analyze_still_rejects_a_missing_file(tmp_path):
    with pytest.raises(inference.InferenceError):
        inference.analyze(str(tmp_path / "nope.jpg"))


def test_analyze_still_rejects_a_file_that_is_not_an_image(tmp_path):
    """LOAD_TRUNCATED_IMAGES must not turn genuinely dead data into a silent success.

    The 4096-byte AppleDouble sidecars and zero-byte files from the same import are supposed to
    keep failing here — they are filtered at scan now, but the sidecar is the second line.
    """
    path = tmp_path / "sidecar.jpg"
    path.write_bytes(b"\x00\x05\x16\x07\x00\x02\x00\x00Mac OS X" + b"\x00" * 64)

    with pytest.raises(inference.InferenceError):
        inference.analyze(str(path))
