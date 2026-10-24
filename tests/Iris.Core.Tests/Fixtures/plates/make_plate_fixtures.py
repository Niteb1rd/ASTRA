"""Makes the SYNTHETIC parity fixtures for multi-row names and plate colours (tests/Iris.Core.Tests/Fixtures/plates/).

Nothing here comes from the game: frames are drawn with OpenCV's Hershey font onto generated backgrounds, with made-up words
("STACKEDHANDLEPLATE"). A multi-row name is the game's way of wrapping a long handle: up to three centered rows above the distance
line, every row above the last one a full-width row (11 line heights or more), each row one word; the name is the rows joined.

Frames:
  plates_a  the good cases: a 3-row plate, a 2-row plate (a full first row and a short second row), a single row plate for reference.
  plates_b  the cases that must NOT join: an off-center row, a junk line (two words) above a name, a name-like row that is not centered
            above a short plate (partial stack: a nameplate without a name), a 4-row stack (one row too many).
  plates_c  the plate colours: BLUE, GREEN, RED, pure WHITE and the light cyan WHITE, single and multi row.

For every frame the Python reference (detector_ref.py, o0_rule on, with the exported letters-model.bin) records the text lines, the pairs, the
row stacks, the evaluation of every pair and the detections (name, rows, colour, hue, saturation). The C# tests compare against that.

The reference lives in the training kit, which is not part of this repository (and is git-ignored there); this script only imports it.
Run (the kit's scripts folder first, then the model, then the output folder; -I keeps the current folder off the import path):

    python3 -I -B make_plate_fixtures.py /path/to/training-data/scripts ../../../../src/Iris.Core/Detection/Letters/letters-model.bin .
"""
import json
import sys
from pathlib import Path

if len(sys.argv) != 4:
    sys.exit(__doc__)
KIT, MODEL, OUT = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3])
sys.path.insert(0, KIT)

import cv2  # noqa: E402
import numpy as np  # noqa: E402

import detector_ref as D  # noqa: E402
from model_io import NumpyMLP  # noqa: E402

FONT = cv2.FONT_HERSHEY_SIMPLEX
CYAN, WHITE = (255, 230, 110), (255, 255, 255)           # BGR
BLUE, GREEN, RED = (240, 190, 150), (80, 185, 80), (70, 70, 235)
ROW_PITCH = 19                                           # top to top of two rows: 1.4 line heights (the game: 19 px at 1440 = 1.36)


def background(h, w, rng):
    g = np.linspace(14, 26, h, dtype=np.float32)[:, None, None]
    bg = np.broadcast_to(g, (h, w, 3)).copy() + np.array([8, 4, 0], np.float32)
    for _ in range(60):
        bg[rng.integers(0, h), rng.integers(0, w)] += rng.integers(60, 140)
    return np.clip(bg, 0, 255).astype(np.uint8)


class Canvas:
    def __init__(self, h, w, scale, rng):
        self.img = background(h, w, rng)
        self.scale = scale
        self.line_h = cv2.getTextSize("H", FONT, scale, 1)[0][1]

    def text(self, s, cx, top, color):
        layer = np.zeros(self.img.shape[:2], np.uint8)
        (w, h), _ = cv2.getTextSize(s, FONT, self.scale, 1)
        cv2.putText(layer, s, (int(cx - w / 2), top + h), FONT, self.scale, 255, 1, cv2.LINE_AA)
        a = (layer.astype(np.float32) / 255)[:, :, None]
        self.img[:] = np.clip(self.img.astype(np.float32) * (1 - a) + np.array(color, np.float32) * a, 0, 255).astype(np.uint8)

    def plate(self, rows, dist, cx, top, color, offsets=None, pitch=ROW_PITCH):
        """rows: the words of the name, top to bottom; offsets: x shift of a row (to make one off center)."""
        offsets = offsets or [0] * len(rows)
        for i, word in enumerate(rows):
            self.text(word, cx + offsets[i], top + i * pitch, color)
        self.text(dist, cx, top + (len(rows) - 1) * pitch + self.line_h + int(self.line_h * 0.7), color)


def frame_a(rng):
    c = Canvas(1440, 1280, 0.62, rng)
    c.plate(["STACKEDHANDLEPLATE", "MIXEDFIXTUREHANDLE", "SAMPLEEND"], "9.0km", 400, 200, CYAN)       # 3 rows, 45 letters
    c.plate(["UPPERLAYERHANDLE", "TAILPIECE"], "4.4km", 820, 200, CYAN)                                 # 2 rows: full first row, short second row
    c.plate(["SINGLEROWPLATE"], "7.7km", 400, 460, CYAN)                                               # one row, as before
    c.plate(["FIRSTLAYERSAMPLE", "SECONDLAYERHANDLE"], "1.9km", 820, 460, WHITE)                       # 2 full rows, white
    return c.img


def frame_b(rng):
    c = Canvas(1440, 1280, 0.62, rng)
    # the top row is 30 px off center: the stack breaks there, the row above is a name-like blocker: a nameplate, but no name
    c.plate(["STACKEDHANDLEPLATE", "MIXEDFIXTUREHANDLE", "SAMPLEEND"], "9.0km", 400, 180, CYAN, offsets=[30, 0, 0])
    # the middle row is off center (and the top row can not be reached through it): the name line alone has a name-like blocker above it
    c.plate(["UPPERLAYERHANDLE", "LASTLAYERSAMPLE", "TAILPIECE"], "4.4km", 820, 180, CYAN, offsets=[0, -40, 0])
    # a junk line above a name: two words, wide and centered. It is not one word, the stack is ignored: the name line alone is read
    c.plate(["TWO WORDS ABOVE THE NAME HERE", "SAMPLEEND"], "5.5km", 400, 420, CYAN)
    # a name-like row, 80 px off center, above a short single row plate: partial stack, a nameplate without a name
    c.plate(["FIRSTLAYERSAMPLE", "TAILPIECE"], "2.2km", 820, 420, CYAN, offsets=[80, 0])
    # four rows: one row too many (a 4th row above is a blocker): a nameplate without a name
    c.plate(["UPPERLAYERHANDLE", "STACKEDHANDLEPLATE", "MIXEDFIXTUREHANDLE", "SAMPLEEND"], "8.2km", 400, 640, CYAN)
    return c.img


def frame_c(rng):
    c = Canvas(1440, 1280, 0.62, rng)
    c.plate(["BLUEPLATESAMPLE"], "6.1km", 400, 200, BLUE)
    c.plate(["UPPERLAYERHANDLE", "TAILPIECE"], "6.2km", 820, 200, BLUE)
    c.plate(["GREENPLATESAMPLE"], "6.3km", 400, 400, GREEN)
    c.plate(["REDPLATESAMPLE"], "6.4km", 820, 400, RED)
    c.plate(["STACKEDHANDLEPLATE", "MIXEDFIXTUREHANDLE", "SAMPLEEND"], "6.5km", 400, 600, RED)
    c.plate(["PUREWHITESAMPLE"], "6.6km", 820, 600, WHITE)
    c.plate(["LIGHTCYANSAMPLE"], "6.7km", 400, 800, CYAN)
    return c.img


FRAMES = [("plates_a", frame_a), ("plates_b", frame_b), ("plates_c", frame_c)]


def rect(b):
    return [int(v) for v in b]


def detection(d):
    return {"name": d.name, "score": d.score, "name_box": rect(d.name_box), "dist_box": rect(d.dist_box), "dist_text": d.dist_text.rstrip("["),
            "speed_group": d.dist_text.endswith("["), "rows": [rect(r) for r in d.rows], "color": d.color, "hue": d.hue, "sat": d.sat}


def describe(img, reader, params):
    a = D.analyze(img, params)
    dets = D.detect(img, reader, params)
    fe = D.FrameEvaluator(img, reader, a, params)
    evaluations = []
    for kind, plist in (("strict", a.pairs), ("crowd", list(a.crowd)), ("loose", a.loose)):
        for ti, bi in plist:
            ok, det = fe.evaluate(a.boxes[ti], a.boxes[bi])
            own, _ = fe.pair(a.boxes[ti], a.boxes[bi])
            e = {"top": rect(a.boxes[ti]), "bottom": rect(a.boxes[bi]), "kind": kind, "pair_is_nameplate": bool(own), "is_nameplate": bool(ok),
                 "name": det.name if det else None, "score": det.score if det else 0.0}
            e["dist_text"] = det.dist_text.rstrip("[") if det else ""
            e["speed_group"] = bool(det and det.dist_text.endswith("["))
            e["rows"] = [rect(r) for r in det.rows] if det else []
            e["color"] = det.color if det else None
            e["hue"] = det.hue if det else 0.0
            e["sat"] = det.sat if det else 0.0
            evaluations.append(e)
    return {
        "boxes": [rect(b) for b in a.boxes],
        "pairs": [list(map(int, p)) for p in a.pairs],
        "loose": [list(map(int, p)) for p in a.loose],
        "crowd": [[int(ti), int(bi), [int(k) for k in need]] for (ti, bi), need in a.crowd.items()],
        "stacks": [{"top": rect(t), "bottom": rect(b), "rows": [rect(r) for r in rows], "blockers": [rect(r) for r in blk]}
                   for (t, b), (rows, blk) in a.stacks.items()],
        "evaluations": evaluations,
        "detections": [detection(d) for d in dets],
    }


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    reader_params = D.Params(o0_rule=True)
    reader = D.Reader(NumpyMLP(MODEL), reader_params)
    default = D.Params(o0_rule=True)
    rng = np.random.default_rng(20261024)
    keys = ("region", "ref_height", "name_min", "name_max", "min_name_score", "center_tol", "left_tol", "left_width_diff", "max_rows",
            "row_gap_max", "row_pitch_min", "row_pitch_max", "row_min_caps", "white_sat", "cyan_hue", "green_sat", "green_hue", "red_hue",
            "blue_hue", "blue_sat", "o0_rule")
    doc = {"params": {k: getattr(default, k) for k in keys}, "frames": []}
    for name, fn in FRAMES:
        img = fn(rng)
        cv2.imwrite(str(OUT / f"{name}.png"), img)
        entry = {"file": f"{name}.png", "width": img.shape[1], "height": img.shape[0], "default": describe(img, reader, default)}
        doc["frames"].append(entry)
        print(name, img.shape[:2])
        for d in entry["default"]["detections"]:
            print("   DET", d["name"], round(d["score"], 3), "rows", len(d["rows"]), d["color"], round(d["hue"], 1), round(d["sat"], 2), d["dist_text"])
        for e in entry["default"]["evaluations"]:
            if e["pair_is_nameplate"] and not e["name"]:
                print("   NAMEPLATE WITHOUT NAME", e["top"], e["bottom"])
        for s in entry["default"]["stacks"]:
            print("   STACK", len(s["rows"]), "rows,", len(s["blockers"]), "blockers", s["top"])
    (OUT / "plates.json").write_text(json.dumps(doc, indent=1), encoding="utf-8")
    print("wrote", OUT)


if __name__ == "__main__":
    main()
