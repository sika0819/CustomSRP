#!/usr/bin/env python3
"""One-shot converter: Boluo uGUI view prefabs -> UI Toolkit UXML/USS.

Drops leaf nodes that have no sprite, no text, and no click.
Positions use anchor percentages plus pixel offsets so they track the parent
the same way RectTransform did inside the 1125x2436 design space.
"""

from __future__ import annotations

import os
import re
import shutil
import codecs
from xml.sax.saxutils import escape

SRC = "/Users/sika/Desktop/code/BoluoProject/UnityProject"
OUT = "/Users/sika/Desktop/code/CustomSRP/Assets/GameMain/UI"
PREFAB_DIR = os.path.join(SRC, "Assets/Arts/Prefabs")

IMAGE_GUID = "fe87c0e1cc204ed48ad3b37840f39efc"
BUTTON_GUID = "4e29b1a8efbd4b44bb3f3716e73f07ff"
TMP_GUID = "f4688fdb7df04437aeb418b961361dc5"
TEXT_GUID = "5f7201a12d95ffc409449d95f23cf332"
SLIDER_GUID = "67db9e8f0e2ae9c40bc1e2b64352a6b4"
SCROLL_GUID = "1aa08ab6e0800fa44ae55d278d1423e3"
RAW_GUID = "1344c3c82d62a2a41a3576d8abb8e3ea"
LOADING_GUID = "41553f8c5b7165b4b852ed7b279e3c74"

FORMS = {
    "InGameControlView": "IslandMainForm",
    "InGameTianQiView": "WeatherForm",
    "InGameYinYueView": "MusicForm",
    "InGameTiaoJieView": "VolumeForm",
    "InGameYuSheView": "PresetForm",
    "InGamePauseTipView": "TimerPauseForm",
}

ITEMS = {
    "YusheItem": "PresetItem",
}

# Longest tokens first. Source object names are pinyin; output names say the role.
TOKENS = [
    ("shijianzhizhen", "timeHand"),
    ("shijiandise", "timeBackground"),
    ("shijianicon", "timeIcon"),
    ("shijiantext", "timeLabel"),
    ("shijian", "timeDial"),
    ("tianqitanchuang", "weatherDialog"),
    ("tianqidise", "weatherBackground"),
    ("tianqiicon", "weatherIcon"),
    ("tianqi", "weather"),
    ("yinyuetanchuang", "musicDialog"),
    ("yinyuedise", "musicBackground"),
    ("yinyueicon", "musicIcon"),
    ("yinyuepause", "musicPause"),
    ("yinyueanima", "musicPlaying"),
    ("yinyue", "music"),
    ("yinxiao", "effect"),
    ("tiaojiexiantiao", "volumeTrack"),
    ("tiaojiedise", "volumeBackground"),
    ("tiaojie", "volume"),
    ("yushetanchuang", "presetDialog"),
    ("yusheicon", "presetIcon"),
    ("yushetext", "presetLabel"),
    ("yushe", "preset"),
    ("dingshidise", "timerBackground"),
    ("dingshiyouren", "timerActive"),
    ("dingshimoren", "timerIdle"),
    ("dingshifillamount", "timerFill"),
    ("dingshi", "timer"),
    ("dingcengmengceng", "topScrim"),
    ("dingbuhengxian", "topRule"),
    ("wodeicon", "profile"),
    ("pausetip", "pauseHint"),
    ("youshang", "topRightDecor"),
    ("dangweizhong", "levelMedium"),
    ("dangweixiao", "levelSmall"),
    ("dangweida", "levelLarge"),
    ("dangweidise", "levelBackground"),
    ("guanbidangwei", "closeLevel"),
    ("guanbi", "close"),
    ("huadongtiaohuadongshi", "scrollTrackActive"),
    ("huadongtiao", "scrollTrack"),
    ("zhuandongbiaopan", "dialSpin"),
    ("yalijianceloading", "pressureLoading"),
    ("mobiledata", "mobileData"),
    ("insufficient", "storageFull"),
    ("download", "download"),
    ("button", "button"),
    ("title", "title"),
    ("close", "close"),
    ("dise", "background"),
    ("icon", "icon"),
    ("zhizhen", "hand"),
    ("mengceng", "scrim"),
    ("tanchuang", "dialog"),
    ("sekuai", "colorSwatch"),
    ("unread", "unreadBadge"),
    ("moren", "defaultState"),
    ("tools", "toolRow"),
    ("toolbtn", "tool"),
    ("activity", "activityEntry"),
    ("font", "caption"),
    ("list", "list"),
    ("drag", "dragHandle"),
    ("des", "description"),
    ("bg", "background"),
    ("barleft", "barLeft"),
    ("barright", "barRight"),
    ("barcenter", "barCenter"),
    ("slider", "slider"),
    ("text", "label"),
    ("icon", "icon"),
    ("page", "page"),
    ("tip", "hint"),
]


def decode_yaml_string(raw: str) -> str:
    raw = raw.strip()
    if len(raw) >= 2 and raw[0] == '"' and raw[-1] == '"':
        body = raw[1:-1]
        try:
            return codecs.decode(body, "unicode_escape")
        except Exception:
            return body
    if len(raw) >= 2 and raw[0] == "'" and raw[-1] == "'":
        return raw[1:-1]
    return raw


def read_yaml_text_value(doc: str) -> str:
    """Read m_text / m_Text, including Unity's rare multiline quoted strings."""
    match = re.search(r"\n\s*m_[Tt]ext: (.*)", doc)
    if not match:
        return ""
    first = match.group(1)
    if not first.startswith('"') or (first.endswith('"') and len(first) > 1):
        return decode_yaml_string(first)
    # Opening quote without a closer on this line — keep reading.
    start = match.end(1)
    body = [first[1:]]
    for line in doc[start:].splitlines(True):
        if '"' in line:
            body.append(line[: line.index('"')])
            break
        body.append(line)
    try:
        value = codecs.decode("".join(body), "unicode_escape")
    except Exception:
        value = "".join(body)
    # Unity sometimes wraps a quoted string across lines and keeps the YAML indent.
    return re.sub(r"\n[ \t]+", "\n", value)


def camel(name: str) -> str:
    parts = re.split(r"[^A-Za-z0-9]+", name)
    parts = [p for p in parts if p]
    if not parts:
        return "element"
    head = parts[0][:1].lower() + parts[0][1:]
    rest = "".join(p[:1].upper() + p[1:] for p in parts[1:])
    out = head + rest
    if not out[0].isalpha():
        out = "element" + out
    return out


def kebab(name: str) -> str:
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1-\2", name)
    s = re.sub(r"[^a-zA-Z0-9]+", "-", s).strip("-").lower()
    return s or "element"


def translate(source: str) -> str:
    key = source.strip()
    lowered = key.lower().replace(" ", "").replace("_", "").replace("-", "")
    if lowered in ("", "--------------"):
        return ""
    replaced = lowered
    for token, eng in TOKENS:
        if token in replaced:
            replaced = replaced.replace(token, eng)
    replaced = re.sub(r"[^a-zA-Z0-9]", "", replaced)
    if not replaced or not re.search(r"[A-Za-z]", replaced):
        return ""
    # Drop leftover digits-only suffixes noise but keep meaning digits like 1 on icons.
    return camel(replaced)


def index_guids():
    roots = [
        os.path.join(SRC, "Assets/Arts_notab/UI"),
        os.path.join(SRC, "Assets/Arts/UI"),
        os.path.join(SRC, "Assets/Arts/BankUpdate"),
        os.path.join(SRC, "Assets/Arts/Font"),
    ]
    found = {}
    for root in roots:
        if not os.path.isdir(root):
            continue
        for dirpath, _, files in os.walk(root):
            for fn in files:
                if not fn.endswith(".meta"):
                    continue
                path = os.path.join(dirpath, fn)
                with open(path, errors="ignore") as handle:
                    head = handle.read(300)
                match = re.search(r"^guid: ([0-9a-f]+)", head, re.M)
                if match:
                    asset = path[:-5]
                    if os.path.isfile(asset):
                        found[match.group(1)] = asset
    return found


def lookup_guid(guid, cache, missing_search):
    if guid in cache:
        return cache[guid]
    if guid in missing_search:
        return missing_search[guid]
    root = os.path.join(SRC, "Assets")
    needle = f"guid: {guid}"
    for dirpath, dirnames, files in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in ("AmplifyImpostors", "Stylized Grass Shader", "GPUInstancerPro", "WanosAudio")]
        for fn in files:
            if not fn.endswith(".meta"):
                continue
            path = os.path.join(dirpath, fn)
            try:
                with open(path, errors="ignore") as handle:
                    if needle not in handle.read(250):
                        continue
            except OSError:
                continue
            asset = path[:-5]
            if os.path.isfile(asset):
                missing_search[guid] = asset
                return asset
    missing_search[guid] = None
    return None


def parse_unity(path):
    text = open(path, errors="ignore").read()
    docs = re.split(r"\n--- !u!", text)
    gameobjects = {}
    rects = {}
    behaviours = {}
    for doc in docs:
        id_match = re.match(r"(\d+) &(-?\d+)", doc)
        if not id_match:
            continue
        kind = int(id_match.group(1))
        file_id = id_match.group(2)
        if kind == 1:
            name = re.search(r"\n\s*m_Name: (.*)", doc)
            active = re.search(r"\n\s*m_IsActive: (\d)", doc)
            comps = re.findall(r"component: \{fileID: (-?\d+)\}", doc)
            gameobjects[file_id] = {
                "name": name.group(1).strip() if name else "",
                "active": active.group(1) == "1" if active else True,
                "components": comps,
            }
        elif kind == 224:
            go = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", doc)
            father = re.search(r"m_Father: \{fileID: (-?\d+)\}", doc)
            if not go:
                continue
            def vec(key, default=(0, 0)):
                m = re.search(key + r": \{x: ([-\d.]+), y: ([-\d.]+)", doc)
                return (float(m.group(1)), float(m.group(2))) if m else default
            rects[file_id] = {
                "go": go.group(1),
                "father": father.group(1) if father else "0",
                "anchor_min": vec("m_AnchorMin"),
                "anchor_max": vec("m_AnchorMax", (1, 1)),
                "anchored": vec("m_AnchoredPosition"),
                "size": vec("m_SizeDelta"),
                "pivot": vec("m_Pivot", (0.5, 0.5)),
            }
        elif kind == 114:
            go = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", doc)
            script = re.search(r"m_Script: \{fileID: \d+, guid: ([0-9a-f]+)", doc)
            if not go or not script:
                continue
            guid = script.group(1)
            info = behaviours.setdefault(go.group(1), {})
            if guid == IMAGE_GUID or guid == RAW_GUID:
                color = re.search(r"m_Color: \{r: ([-\d.]+), g: ([-\d.]+), b: ([-\d.]+), a: ([-\d.]+)", doc)
                sprite = re.search(r"m_Sprite: \{fileID: \d+, guid: ([0-9a-f]+)", doc)
                if guid == RAW_GUID:
                    sprite = re.search(r"m_Texture: \{fileID: \d+, guid: ([0-9a-f]+)", doc)
                ray = re.search(r"m_RaycastTarget: (\d)", doc)
                image_type = re.search(r"\n\s*m_Type: (\d+)", doc)
                preserve = re.search(r"\n\s*m_PreserveAspect: (\d)", doc)
                fill_method = re.search(r"\n\s*m_FillMethod: (\d+)", doc)
                fill_amount = re.search(r"\n\s*m_FillAmount: ([-\d.]+)", doc)
                info["image"] = {
                    "color": tuple(float(x) for x in color.groups()) if color else (1, 1, 1, 1),
                    "sprite": sprite.group(1) if sprite else None,
                    "raycast": ray.group(1) == "1" if ray else False,
                    "type": int(image_type.group(1)) if image_type else 0,
                    "preserve": preserve.group(1) == "1" if preserve else False,
                    "fill_method": int(fill_method.group(1)) if fill_method else 0,
                    "fill_amount": float(fill_amount.group(1)) if fill_amount else 1,
                }
            elif guid == BUTTON_GUID:
                method = re.search(r"m_MethodName: (\w+)", doc)
                info["button"] = method.group(1) if method else ""
            elif guid in (TMP_GUID, TEXT_GUID):
                size = re.search(r"\n\s*m_[Ff]ontSize: ([-\d.]+)", doc)
                color = re.search(r"m_fontColor: \{r: ([-\d.]+), g: ([-\d.]+), b: ([-\d.]+), a: ([-\d.]+)", doc)
                if not color:
                    color = re.search(r"m_Color: \{r: ([-\d.]+), g: ([-\d.]+), b: ([-\d.]+), a: ([-\d.]+)", doc)
                halign = re.search(r"\n\s*m_HorizontalAlignment: (\d+)", doc)
                valign = re.search(r"\n\s*m_VerticalAlignment: (\d+)", doc)
                legacy = re.search(r"\n\s*m_Alignment: (\d+)", doc)
                info["text"] = {
                    "value": read_yaml_text_value(doc),
                    "size": float(size.group(1)) if size else 36,
                    "color": tuple(float(x) for x in color.groups()) if color else (1, 1, 1, 1),
                    "halign": int(halign.group(1)) if halign else None,
                    "valign": int(valign.group(1)) if valign else None,
                    "legacy": int(legacy.group(1)) if legacy else None,
                }
            elif guid == SLIDER_GUID:
                value = re.search(r"\n\s*m_Value: ([-\d.]+)", doc)
                info["slider"] = float(value.group(1)) if value else 0.5
            elif guid == SCROLL_GUID:
                info["scroll"] = True
            elif guid == LOADING_GUID:
                info["loading"] = True
        elif kind == 225:
            go = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", doc)
            alpha = re.search(r"\n\s*m_Alpha: ([-\d.]+)", doc)
            if go and alpha:
                behaviours.setdefault(go.group(1), {})["opacity"] = float(alpha.group(1))
    nodes = {}
    for rect_id, rect in rects.items():
        go_id = rect["go"]
        go = gameobjects.get(go_id)
        if not go:
            continue
        nodes[rect_id] = {
            "id": rect_id,
            "go": go_id,
            "name": go["name"],
            "active": go["active"],
            "father": rect["father"],
            "rect": rect,
            "behaviour": behaviours.get(go_id, {}),
            "children": [],
        }
    roots = []
    for node in nodes.values():
        parent = nodes.get(node["father"])
        if parent:
            parent["children"].append(node)
        else:
            roots.append(node)
    return roots, nodes


def has_content(node):
    beh = node["behaviour"]
    image = beh.get("image")
    text = beh.get("text")
    if beh.get("button") is not None or beh.get("slider") is not None or beh.get("scroll"):
        return True
    if text and text["value"].strip():
        return True
    if image and image["sprite"] and image["color"][3] > 0.01:
        return True
    if image and image["raycast"] and image["color"][3] > 0.01:
        return True
    return False


def subtree_useful(node):
    if has_content(node):
        return True
    return any(subtree_useful(child) for child in node["children"])


def px(value):
    text = f"{value:.2f}".rstrip("0").rstrip(".")
    if text in ("-0", ""):
        text = "0"
    return text + "px"


def pct(value):
    text = f"{value * 100:.4f}".rstrip("0").rstrip(".")
    if text in ("-0", ""):
        text = "0"
    return text + "%"


def place(rules, edge, margin, percent, pixels):
    # calc(percent + pixels) is dropped by UI Toolkit. Percent goes on the edge
    # and the pixel offset goes on the matching margin.
    rules.append(f"{edge}: {pct(percent) if abs(percent) > 0.0001 else '0'};")
    rules.append(f"{margin}: {px(pixels) if abs(pixels) > 0.05 else '0'};")


def layout_rules(rect):
    anchor_min = rect["anchor_min"]
    anchor_max = rect["anchor_max"]
    anchored = rect["anchored"]
    size = rect["size"]
    pivot = rect["pivot"]
    offset_min_x = anchored[0] - size[0] * pivot[0]
    offset_min_y = anchored[1] - size[1] * pivot[1]
    offset_max_x = anchored[0] + size[0] * (1 - pivot[0])
    offset_max_y = anchored[1] + size[1] * (1 - pivot[1])
    stretch_x = abs(anchor_max[0] - anchor_min[0]) > 0.001
    stretch_y = abs(anchor_max[1] - anchor_min[1]) > 0.001
    rules = ["position: absolute;"]
    place(rules, "left", "margin-left", anchor_min[0], offset_min_x)
    place(rules, "top", "margin-top", 1 - anchor_max[1], -offset_max_y)
    if stretch_x:
        place(rules, "right", "margin-right", 1 - anchor_max[0], -offset_max_x)
    else:
        rules.append("margin-right: 0;")
        if abs(size[0]) > 0.5:
            rules.append("width: " + px(size[0]) + ";")
    if stretch_y:
        place(rules, "bottom", "margin-bottom", anchor_min[1], offset_min_y)
    else:
        rules.append("margin-bottom: 0;")
        if abs(size[1]) > 0.5:
            rules.append("height: " + px(size[1]) + ";")
    return rules


def align_text(text):
    if text["legacy"] is not None:
        legacy = text["legacy"]
        horizontal = ("left", "center", "right")[legacy % 3]
        vertical = ("upper", "middle", "lower")[min(legacy // 3, 2)]
    else:
        h = text["halign"] or 2
        v = text["valign"] or 512
        horizontal = {1: "left", 2: "center", 4: "right"}.get(h, "center")
        vertical = {256: "upper", 512: "middle", 1024: "lower"}.get(v, "middle")
    vmap = {"upper": "flex-start", "middle": "center", "lower": "flex-end"}
    hmap = {"left": "flex-start", "center": "center", "right": "flex-end"}
    unity = {
        ("upper", "left"): "upper-left",
        ("upper", "center"): "upper-center",
        ("upper", "right"): "upper-right",
        ("middle", "left"): "middle-left",
        ("middle", "center"): "middle-center",
        ("middle", "right"): "middle-right",
        ("lower", "left"): "lower-left",
        ("lower", "center"): "lower-center",
        ("lower", "right"): "lower-right",
    }[(vertical, horizontal)]
    return unity, hmap[horizontal], vmap[vertical]


def rgba(color):
    r, g, b, a = [max(0, min(1, c)) for c in color]
    return f"rgba({int(r * 255)}, {int(g * 255)}, {int(b * 255)}, {a:.3f})"


def norm_name(name):
    return re.sub(r"[^a-z0-9]", "", name.lower())


NOTICE_NAMES = {
    "mobiledatainusepage",
    "filecorruptedpage",
    "insufficientstoragepage",
    "storagefullstoragepage",
    "wifidatainusepage",
    "dingshitanchuang",
}
HUD_NAMES = {"tianqidise", "yinyuedise", "tiaojiedise", "wodeicon", "dingshidise"}
LEVEL_NAMES = {"dangweixiao", "dangweizhong", "dangweida", "guanbidangwei", "drag"}
DESIGN = (1125.0, 2436.0)

# Filled after build_templates(). Maps a slot to its canonical parent-local rect and paint.
CANON = {}
COMPONENTS = os.path.join(OUT, "Components")
FORMS_DIR = os.path.join(OUT, "Forms")


def expected_rect(node, pw, ph):
    rect = node["rect"]
    amin, amax = rect["anchor_min"], rect["anchor_max"]
    anchored, size, pivot = rect["anchored"], rect["size"], rect["pivot"]
    offset_min_x = anchored[0] - size[0] * pivot[0]
    offset_max_x = anchored[0] + size[0] * (1 - pivot[0])
    offset_min_y = anchored[1] - size[1] * pivot[1]
    offset_max_y = anchored[1] + size[1] * (1 - pivot[1])
    left = pw * amin[0] + offset_min_x
    right = pw * amax[0] + offset_max_x
    bottom = ph * amin[1] + offset_min_y
    top_u = ph * amax[1] + offset_max_y
    return (left, ph - top_u, right - left, top_u - bottom)


def close_rect(a, b, tol=1.0):
    return all(abs(x - y) <= tol for x, y in zip(a, b))


def sprite_border(asset):
    meta = asset + ".meta"
    if not asset or not os.path.isfile(meta):
        return (0.0, 0.0, 0.0, 0.0)
    text = open(meta, errors="ignore").read()
    match = re.search(
        r"spriteBorder: \{x: ([-\d.]+), y: ([-\d.]+), z: ([-\d.]+), w: ([-\d.]+)\}",
        text,
    )
    if not match:
        return (0.0, 0.0, 0.0, 0.0)
    # Unity sprite border is left, bottom, right, top.
    return tuple(float(v) for v in match.groups())


def copy_sprite(guid, name, copy_dir, copied, guid_cache, missing, url_dir):
    if not guid:
        return None, None
    asset = lookup_guid(guid, guid_cache, missing)
    if not asset:
        return None, None
    ext = os.path.splitext(asset)[1] or ".png"
    dest_name = kebab(name) + ext
    if dest_name in copied and copied[dest_name] != asset:
        dest_name = kebab(name) + "-" + guid[:6] + ext
    dest = os.path.join(copy_dir, dest_name)
    if dest_name not in copied:
        os.makedirs(copy_dir, exist_ok=True)
        shutil.copy2(asset, dest)
        meta = asset + ".meta"
        if os.path.isfile(meta):
            shutil.copy2(meta, dest + ".meta")
        copied[dest_name] = asset
    rel = os.path.relpath(dest, url_dir).replace(os.sep, "/")
    return rel, asset


def image_paint(image, rel, asset, quote='"'):
    if not image:
        return []
    rules = []
    if not rel:
        if image["color"][3] > 0.01:
            rules.append(f"background-color: {rgba(image['color'])};")
        return rules
    rules.append(f"background-image: url({quote}{rel}{quote});")
    mode = "scale-to-fit" if image.get("preserve") else "stretch-to-fill"
    rules.append(f"-unity-background-scale-mode: {mode};")
    rules.append(f"-unity-background-image-tint-color: {rgba(image['color'])};")
    # Sliced sprites keep the border stored on the copied sprite.
    if image.get("type") == 1 and asset:
        left, bottom, right, top = sprite_border(asset)
        if any(v > 0.5 for v in (left, bottom, right, top)):
            rules.append(f"-unity-slice-left: {px(left)};")
            rules.append(f"-unity-slice-right: {px(right)};")
            rules.append(f"-unity-slice-top: {px(top)};")
            rules.append(f"-unity-slice-bottom: {px(bottom)};")
    return rules


def text_paint(node, quote='"'):
    text = node["behaviour"].get("text")
    if not text:
        return []
    unity_align, _, _ = align_text(text)
    size = node["rect"]["size"]
    pivot = node["rect"]["pivot"]
    point = abs(size[0]) <= 0.5 and abs(size[1]) <= 0.5
    rules = [
        f"-unity-text-align: {unity_align};",
        f"font-size: {text['size']:.0f}px;",
        f"color: {rgba(text['color'])};",
        "white-space: nowrap;" if point else "white-space: normal;",
        f"-unity-font: url({quote}../Fonts/pingfangsc-medium.ttf{quote});",
    ]
    if point:
        # Place the pivot on the zero-size rect's point, matching uGUI TMP.
        rules.append("overflow: visible;")
        rules.append("min-width: 0;")
        rules.append("min-height: 0;")
        rules.append(f"translate: {-pivot[0] * 100:.4g}% {-(1 - pivot[1]) * 100:.4g}%;")
    return rules


def opacity_paint(node):
    alpha = node["behaviour"].get("opacity")
    if alpha is None or alpha >= 0.999:
        return []
    return [f"opacity: {alpha:.3f};"]


def pixel_box(rect):
    left, top, width, height = rect
    return [
        "position: absolute;",
        f"left: {px(left)};",
        f"top: {px(top)};",
        f"width: {px(width)};",
        f"height: {px(height)};",
        "margin-left: 0;",
        "margin-top: 0;",
        "margin-right: 0;",
        "margin-bottom: 0;",
    ]


def join_style(rules):
    return " ".join(rule if rule.endswith(";") else rule + ";" for rule in rules)


def css_block(selector, rules):
    return f"{selector} {{\n    " + "\n    ".join(rules) + "\n}"


def find_named(nodes, name):
    hits = [node for node in nodes.values() if node["name"] == name]
    if not hits:
        return None
    return max(hits, key=lambda node: len(node["children"]))


def child_named(node, name):
    for child in node["children"]:
        if child["name"] == name:
            return child
    return None


def notice_role(node):
    key = norm_name(node["name"])
    if key in ("tipicon", "icon"):
        return "icon"
    if "title" in key:
        return "title"
    if key in ("textinfo", "des"):
        return "body"
    if node["name"] == "Button":
        return "primary"
    if node["name"] == "Button1":
        return "secondary"
    return None


def label_of(node):
    text = node["behaviour"].get("text")
    if text and text["value"].strip():
        return node
    for child in node["children"]:
        child_text = child["behaviour"].get("text")
        if child_text and child_text["value"].strip():
            return child
    return None


def button_reset():
    return [
        "padding: 0;",
        "border-width: 0;",
        "min-width: 0;",
        "min-height: 0;",
        "background-color: rgba(0, 0, 0, 0);",
    ]


def build_templates(guid_cache, missing):
    os.makedirs(COMPONENTS, exist_ok=True)
    build_notice(guid_cache, missing)
    build_hud(guid_cache, missing)
    build_volume(guid_cache, missing)
    build_level(guid_cache, missing)


def build_notice(guid_cache, missing):
    path = os.path.join(PREFAB_DIR, "View", "InGameMobileDataView.prefab")
    _, nodes = parse_unity(path)
    card = find_named(nodes, "MobileDataInUsePage")
    card_rect = expected_rect(card, *DESIGN)
    copy_dir = os.path.join(OUT, "Textures", "NoticeCard")
    copied = {}
    slots = {}
    for child in card["children"]:
        role = notice_role(child)
        if role:
            slots[role] = child
    classes = [css_block(".notice-card", ["overflow: visible;"])]
    # Card chrome lives on the template. Instances override it when the sprite differs.
    root_image = card["behaviour"].get("image")
    rel, asset = copy_sprite(
        root_image.get("sprite") if root_image else None,
        "panel",
        copy_dir,
        copied,
        guid_cache,
        missing,
        COMPONENTS,
    )
    classes.append(css_block(".notice-card", image_paint(root_image, rel, asset) or ["overflow: visible;"]))
    uxml = [
        '<ui:UXML xmlns:ui="UnityEngine.UIElements">',
        '    <Style src="NoticeCard.uss" />',
        '    <ui:VisualElement name="noticeCard" class="notice-card">',
    ]
    order = ("icon", "title", "body", "primary", "secondary")
    CANON["NoticeCard"] = {"panel": root_image.get("sprite") if root_image else None, "slots": {}}
    for role in order:
        node = slots[role]
        rect = expected_rect(node, card_rect[2], card_rect[3])
        CANON["NoticeCard"]["slots"][role] = {
            "rect": rect,
            "sprite": (node["behaviour"].get("image") or {}).get("sprite"),
            "text": text_paint(node),
            "value": (label_of(node)["behaviour"]["text"]["value"] if label_of(node) else ""),
        }
        rules = pixel_box(rect) + image_paint(
            node["behaviour"].get("image"),
            *copy_sprite(
                (node["behaviour"].get("image") or {}).get("sprite"),
                role,
                copy_dir,
                copied,
                guid_cache,
                missing,
                COMPONENTS,
            ),
        )
        rules += text_paint(node)
        tag = "ui:Button" if role in ("primary", "secondary") else "ui:Label" if node["behaviour"].get("text") else "ui:VisualElement"
        if tag == "ui:Button":
            rules += button_reset()
        classes.append(css_block(f".notice-card__{kebab(role)}", rules))
        if tag == "ui:Button":
            caption = label_of(node)
            caption_text = escape(caption["behaviour"]["text"]["value"], {'"': "&quot;"}) if caption else ""
            classes.append(css_block(
                f".notice-card__{kebab(role)}-label",
                ["position: absolute;", "left: 0;", "right: 0;", "top: 0;", "bottom: 0;", "picking-mode: Ignore;"]
                + (text_paint(caption) if caption else []),
            ))
            uxml.append(
                f'        <{tag} name="{role}" class="notice-card__{kebab(role)}">'
            )
            uxml.append(
                f'            <ui:Label name="{role}Label" class="notice-card__{kebab(role)}-label" text="{caption_text}" />'
            )
            uxml.append(f"        </{tag}>")
        elif tag == "ui:Label":
            value = escape(node["behaviour"]["text"]["value"], {'"': "&quot;"})
            uxml.append(f'        <ui:Label name="{role}" class="notice-card__{kebab(role)}" text="{value}" />')
        else:
            uxml.append(f'        <ui:VisualElement name="{role}" class="notice-card__{kebab(role)}" />')
    uxml.append("    </ui:VisualElement>")
    uxml.append("</ui:UXML>")
    open(os.path.join(COMPONENTS, "NoticeCard.uxml"), "w").write("\n".join(uxml) + "\n")
    open(os.path.join(COMPONENTS, "NoticeCard.uss"), "w").write("\n\n".join(classes) + "\n")


def build_hud(guid_cache, missing):
    path = os.path.join(PREFAB_DIR, "View", "InGameControlView.prefab")
    _, nodes = parse_unity(path)
    icons = {}
    for node in nodes.values():
        if node["name"] not in ("tianqidise", "yinyuedise", "tiaojiedise", "wodeicon", "dingshidise"):
            continue
        current = icons.get(node["name"])
        if current is None or len(node["children"]) > len(current["children"]):
            icons[node["name"]] = node
    copy_dir = os.path.join(OUT, "Textures", "HudIcon")
    copied = {}
    weather = icons["tianqidise"]
    plate = child_named(weather, "tianqidise")
    glyph = child_named(weather, "tianqiicon")
    plate_sprite = (plate["behaviour"].get("image") or {}).get("sprite")
    CANON["HudIcon"] = {"plate": plate_sprite, "glyph": (glyph["behaviour"].get("image") or {}).get("sprite")}

    def paint_node(node, slot):
        image = node["behaviour"].get("image")
        rel, asset = copy_sprite(
            (image or {}).get("sprite"), slot, copy_dir, copied, guid_cache, missing, COMPONENTS
        )
        return pixel_box(expected_rect(node, 108, 108)) + image_paint(image, rel, asset) + text_paint(node)

    classes = [css_block(".hud-icon", ["overflow: visible;"])]
    classes.append(css_block(".hud-icon__plate", paint_node(plate, "plate")))
    classes.append(css_block(".hud-icon__glyph", paint_node(glyph, "glyph")))

    music = icons["yinyuedise"]
    pause = child_named(music, "yinyuePause")
    playing = child_named(music, "yinyueAnima")
    classes.append(css_block(".hud-icon__music-pause", paint_node(pause, "music-pause") + ["display: none;"]))
    playing_rules = pixel_box(expected_rect(playing, 108, 108)) + ["display: none;"]
    classes.append(css_block(".hud-icon__music-playing", playing_rules))
    bar_xml = []
    for bar in playing["children"]:
        if not subtree_useful(bar):
            continue
        slot = kebab(translate(bar["name"]) or bar["name"])
        classes.append(css_block(f".hud-icon__{slot}", paint_node(bar, slot)))
        bar_xml.append(f'            <ui:VisualElement name="{translate(bar["name"]) or slot}" class="hud-icon__{slot}" />')

    volume = icons["tiaojiedise"]
    marks = child_named(volume, "tiaojie")
    classes.append(css_block(".hud-icon__volume-marks", pixel_box(expected_rect(marks, 108, 108)) + ["display: none;"]))
    # Tracks first, then circular knobs so handles paint above progress bars.
    mark_nodes = [m for m in marks["children"] if subtree_useful(m)]
    def _volume_z(mark):
        name = norm_name(mark["name"])
        if "yuan" in name:
            return 2
        if "xiantiao" in name or "track" in name:
            return 0
        return 1
    mark_nodes.sort(key=_volume_z)
    mark_xml = []
    for mark in mark_nodes:
        slot = kebab(translate(mark["name"]) or mark["name"])
        classes.append(css_block(f".hud-icon__{slot}", paint_node(mark, slot)))
        mark_xml.append(f'            <ui:VisualElement name="{translate(mark["name"]) or slot}" class="hud-icon__{slot}" />')

    timer = icons["dingshidise"]
    active = child_named(timer, "dingshiyouren")
    idle = child_named(timer, "dingshimoren")
    classes.append(css_block(".hud-icon__timer-active", pixel_box(expected_rect(active, 108, 108)) + ["display: none;"]))
    classes.append(css_block(".hud-icon__timer-idle", paint_node(idle, "timer-idle") + ["display: none;"]))
    timer_xml = []
    for part in active["children"]:
        if not subtree_useful(part):
            continue
        slot_name = translate(part["name"]) or kebab(part["name"])
        # The filled ring keeps the name the timer logic looks up.
        if norm_name(part["name"]) == "dingshifillamount":
            slot_name = "timerFill"
        classes.append(css_block(f".hud-icon__{kebab(slot_name)}", paint_node(part, kebab(slot_name))))
        timer_xml.append(f'            <ui:VisualElement name="{slot_name}" class="hud-icon__{kebab(slot_name)}" />')

    profile = icons["wodeicon"]
    badge = child_named(profile, "unread")
    classes.append(css_block(".hud-icon__badge", paint_node(badge, "badge") + ["display: none;"]))

    uxml = [
        '<ui:UXML xmlns:ui="UnityEngine.UIElements">',
        '    <Style src="HudIcon.uss" />',
        '    <ui:VisualElement name="hudIcon" class="hud-icon">',
        '        <ui:VisualElement name="plate" class="hud-icon__plate" />',
        '        <ui:VisualElement name="glyph" class="hud-icon__glyph" />',
        '        <ui:VisualElement name="musicPause" class="hud-icon__music-pause" />',
        '        <ui:VisualElement name="musicPlaying" class="hud-icon__music-playing">',
        *bar_xml,
        "        </ui:VisualElement>",
        '        <ui:VisualElement name="volumeMarks" class="hud-icon__volume-marks">',
        *mark_xml,
        "        </ui:VisualElement>",
        '        <ui:VisualElement name="timerActive" class="hud-icon__timer-active">',
        *timer_xml,
        "        </ui:VisualElement>",
        '        <ui:VisualElement name="timerIdle" class="hud-icon__timer-idle" />',
        '        <ui:Label name="badge" class="hud-icon__badge" text="●" />',
        "    </ui:VisualElement>",
        "</ui:UXML>",
    ]
    open(os.path.join(COMPONENTS, "HudIcon.uxml"), "w").write("\n".join(uxml) + "\n")
    open(os.path.join(COMPONENTS, "HudIcon.uss"), "w").write("\n\n".join(classes) + "\n")


def build_volume(guid_cache, missing):
    path = os.path.join(PREFAB_DIR, "View", "InGameTiaoJieView.prefab")
    _, nodes = parse_unity(path)
    track = find_named(nodes, "disetiao1")
    knob = find_named(nodes, "yushedian1")
    value = find_named(nodes, "font11")
    caption = find_named(nodes, "font1")
    members = {"track": track, "knob": knob, "value": value, "caption": caption}
    canvas = {}
    for role, node in members.items():
        canvas[role] = expected_rect(node, *DESIGN)
    left = min(item[0] for item in canvas.values())
    top = min(item[1] for item in canvas.values())
    right = max(item[0] + item[2] for item in canvas.values())
    bottom = max(item[1] + item[3] for item in canvas.values())
    CANON["VolumeRow"] = {
        "origin": (left, top),
        "size": (right - left, bottom - top),
        "local": {role: (rect[0] - left, rect[1] - top, rect[2], rect[3]) for role, rect in canvas.items()},
        "fill": expected_rect(child_named(track, "huangdongyanse1"), canvas["track"][2], canvas["track"][3]),
        "caption": caption["behaviour"]["text"]["value"],
        "value": value["behaviour"]["text"]["value"],
    }
    copy_dir = os.path.join(OUT, "Textures", "VolumeRow")
    copied = {}

    def painted(node, parent_size, slot):
        image = node["behaviour"].get("image")
        rel, asset = copy_sprite((image or {}).get("sprite"), slot, copy_dir, copied, guid_cache, missing, COMPONENTS)
        return pixel_box(expected_rect(node, *parent_size)) + image_paint(image, rel, asset) + text_paint(node)

    classes = [css_block(".volume-row", ["overflow: visible;"])]
    row_size = CANON["VolumeRow"]["size"]
    classes.append(css_block(".volume-row__track", painted(track, row_size, "track")[0:0] or []))
    # painted() used the canvas parent; rebuild locals against the row and the track.
    local = CANON["VolumeRow"]["local"]
    track_image = track["behaviour"].get("image")
    rel, asset = copy_sprite((track_image or {}).get("sprite"), "track", copy_dir, copied, guid_cache, missing, COMPONENTS)
    classes[-1] = css_block(".volume-row__track", pixel_box(local["track"]) + image_paint(track_image, rel, asset))
    fill = child_named(track, "huangdongyanse1")
    plate = child_named(track, "disetiao1")
    classes.append(css_block(".volume-row__fill", painted(fill, (canvas["track"][2], canvas["track"][3]), "fill")))
    classes.append(css_block(".volume-row__track-plate", painted(plate, (canvas["track"][2], canvas["track"][3]), "track-plate")))
    knob_image = knob["behaviour"].get("image")
    rel, asset = copy_sprite((knob_image or {}).get("sprite"), "knob", copy_dir, copied, guid_cache, missing, COMPONENTS)
    classes.append(css_block(".volume-row__knob", pixel_box(local["knob"]) + image_paint(knob_image, rel, asset) + ["picking-mode: Position;"]))
    face = knob["children"][0]
    classes.append(css_block(".volume-row__knob-face", painted(face, (canvas["knob"][2], canvas["knob"][3]), "knob-face")))
    classes.append(css_block(".volume-row__value", pixel_box(local["value"]) + text_paint(value)))
    classes.append(css_block(".volume-row__caption", pixel_box(local["caption"]) + text_paint(caption)))
    value_text = escape(value["behaviour"]["text"]["value"], {'"': "&quot;"})
    caption_text = escape(caption["behaviour"]["text"]["value"], {'"': "&quot;"})
    uxml = f'''<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <Style src="VolumeRow.uss" />
    <ui:VisualElement name="volumeRow" class="volume-row">
        <ui:Label name="caption" class="volume-row__caption" text="{caption_text}" />
        <ui:Label name="value" class="volume-row__value" text="{value_text}" />
        <ui:VisualElement name="track" class="volume-row__track">
            <ui:VisualElement name="trackPlate" class="volume-row__track-plate" />
            <ui:VisualElement name="fill" class="volume-row__fill" />
        </ui:VisualElement>
        <ui:VisualElement name="knob" class="volume-row__knob">
            <ui:VisualElement name="knobFace" class="volume-row__knob-face" />
        </ui:VisualElement>
    </ui:VisualElement>
</ui:UXML>
'''
    open(os.path.join(COMPONENTS, "VolumeRow.uxml"), "w").write(uxml)
    open(os.path.join(COMPONENTS, "VolumeRow.uss"), "w").write("\n\n".join(classes) + "\n")


def build_level(guid_cache, missing):
    path = os.path.join(PREFAB_DIR, "View", "InGameTianQiView.prefab")
    _, nodes = parse_unity(path)
    drag = find_named(nodes, "drag")
    highlight = child_named(drag, "dian")
    label = None
    for child in drag["children"]:
        if child["behaviour"].get("text"):
            label = child
            break
    copy_dir = os.path.join(OUT, "Textures", "LevelKnob")
    copied = {}
    drag_rect = expected_rect(drag, *DESIGN)
    highlight_rect = expected_rect(highlight, drag_rect[2], drag_rect[3])
    label_rect = expected_rect(label, drag_rect[2], drag_rect[3])
    CANON["LevelKnob"] = {
        "highlight": highlight_rect,
        "label": label_rect,
        "text": label["behaviour"]["text"]["value"],
        "sprite": (highlight["behaviour"].get("image") or {}).get("sprite"),
    }
    image = highlight["behaviour"].get("image")
    rel, asset = copy_sprite(image.get("sprite") if image else None, "highlight", copy_dir, copied, guid_cache, missing, COMPONENTS)
    classes = [
        css_block(".level-knob", ["overflow: visible;"]),
        css_block(".level-knob__highlight", pixel_box(highlight_rect) + image_paint(image, rel, asset) + ["display: none;"]),
        css_block(".level-knob__label", pixel_box(label_rect) + text_paint(label) + ["display: none;", "picking-mode: Ignore;"]),
    ]
    text = escape(label["behaviour"]["text"]["value"], {'"': "&quot;"})
    uxml = f'''<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <Style src="LevelKnob.uss" />
    <ui:VisualElement name="levelKnob" class="level-knob">
        <ui:VisualElement name="highlight" class="level-knob__highlight" />
        <ui:Label name="label" class="level-knob__label" text="{text}" />
    </ui:VisualElement>
</ui:UXML>
'''
    open(os.path.join(COMPONENTS, "LevelKnob.uxml"), "w").write(uxml)
    open(os.path.join(COMPONENTS, "LevelKnob.uss"), "w").write("\n\n".join(classes) + "\n")


def choose_name(node, form_name, used, parent_name=""):
    source = node["name"]
    beh = node["behaviour"]
    method = beh.get("button") or ""
    translated = translate(source)
    generic = not translated or translated.lower().startswith("button") or translated in ("image", "label", "group", "text")
    if generic:
        parent_translated = translate(parent_name)
        if parent_translated and not parent_translated.lower().startswith("button"):
            translated = parent_translated
    name = translated or ""
    if method in ("Btn_Close", "Close", "OnClose", "Hide"):
        name = "closeButton"
    elif beh.get("scroll"):
        name = "musicList" if form_name == "MusicForm" else "presetList" if form_name == "PresetForm" else (name or "scrollList")
    elif beh.get("slider") is not None:
        blob = (source + method).lower()
        if "yinxiao" in blob or "effect" in blob or "voice" in blob:
            name = "effectVolumeSlider"
        elif "yinyue" in blob or "bgm" in blob or "music" in blob:
            name = "backgroundVolumeSlider"
        else:
            name = name or "volumeSlider"
            if not name.endswith("Slider"):
                name += "Slider"
    elif method or "button" in beh:
        if not name:
            name = "button"
        if "weather" in name:
            name = "weatherButton"
        elif "music" in name and "List" not in name:
            name = "musicButton"
        elif "volume" in name:
            name = "volumeButton"
        elif "preset" in name:
            name = "presetButton"
        elif "timer" in name or "pause" in name:
            name = "timerButton" if form_name == "IslandMainForm" else name
        elif "profile" in name:
            name = "profileButton"
        elif name == "close":
            name = "closeButton"
        elif not name.endswith("Button") and not name.endswith("Slider"):
            name = name + "Button"
    elif not name:
        if beh.get("text"):
            name = "label"
        elif beh.get("image"):
            name = "image"
        else:
            name = "group"
    if name in used:
        index = 2
        while f"{name}{index}" in used:
            index += 1
        name = f"{name}{index}"
    used.add(name)
    return name


RECORDS = []


def template_of(node):
    key = norm_name(node["name"])
    if key in NOTICE_NAMES:
        return "NoticeCard"
    if node["name"] in HUD_NAMES:
        return "HudIcon"
    if node["name"] in LEVEL_NAMES:
        return "LevelKnob"
    return None


def record(node, parent_size, style_rules):
    RECORDS.append({
        "id": node["id"],
        "name": node["name"],
        "parent": parent_size,
        "expected": expected_rect(node, *parent_size),
        "style": style_rules,
    })


def resolve_style(rules, pw, ph):
    props = {}
    for rule in rules:
        if ":" not in rule:
            continue
        key, value = rule.split(":", 1)
        props[key.strip()] = value.strip().rstrip(";").strip()

    def length(prop, axis):
        raw = props.get(prop)
        if raw is None:
            return None
        if raw.endswith("%"):
            return float(raw[:-1]) / 100.0 * (pw if axis == "x" else ph)
        if raw.endswith("px"):
            return float(raw[:-2])
        try:
            return float(raw)
        except ValueError:
            return 0.0

    left = (length("left", "x") or 0) + (length("margin-left", "x") or 0)
    top = (length("top", "y") or 0) + (length("margin-top", "y") or 0)
    width = length("width", "x")
    height = length("height", "y")
    if width is None and length("right", "x") is not None:
        right = (length("right", "x") or 0) + (length("margin-right", "x") or 0)
        width = pw - right - left
    if height is None and length("bottom", "y") is not None:
        bottom = (length("bottom", "y") or 0) + (length("margin-bottom", "y") or 0)
        height = ph - bottom - top
    return (left, top, 0 if width is None else width, 0 if height is None else height)


def instance_lines(template, name, style, overrides, depth):
    pad = "    " * depth
    open_tag = f'{pad}<ui:Instance template="{template}" name="{name}" style="{style}">'
    body = [open_tag]
    for element, attrs in overrides:
        parts = [f'element-name="{element}"']
        for key, value in attrs.items():
            text = escape(value, {'"': "&quot;"}).replace("\n", "&#10;")
            parts.append(f'{key}="{text}"')
        body.append(f"{pad}    <ui:AttributeOverrides {' '.join(parts)} />")
    body.append(f"{pad}</ui:Instance>")
    return body


def image_override(image, slot, copy_dir, copied, guid_cache, missing):
    if not image or not image.get("sprite"):
        return []
    rel, asset = copy_sprite(image["sprite"], slot, copy_dir, copied, guid_cache, missing, FORMS_DIR)
    return image_paint(image, rel, asset)


def add_delta(rules, selector, declarations):
    # Template style attributes are not overridable in this UI Toolkit version.
    # A name selector beats the template class, so only the prefab delta is written here.
    if declarations:
        rules.append(css_block(selector, declarations))


def emit_notice(node, form_name, used, guid_cache, missing, lines, rules, depth, copy_dir, copied, parent_name, parent_size, templates):
    slots = {}
    for child in node["children"]:
        if not subtree_useful(child):
            continue
        role = notice_role(child)
        if role is None:
            return False
        slots[role] = child
    if "title" not in slots or "body" not in slots or "primary" not in slots:
        return False
    templates.add("NoticeCard")
    card_rect = expected_rect(node, *parent_size)
    notice_ids = {
        "TimerPauseForm": "timerDialog",
    }
    name = notice_ids.get(form_name) or choose_name(node, form_name, used, parent_name)
    used.add(name)
    root_rules = layout_rules(node["rect"]) + opacity_paint(node)
    if not node["active"]:
        root_rules.append("display: none;")
    root_image = node["behaviour"].get("image")
    overrides = []
    canon = CANON["NoticeCard"]
    if root_image and root_image.get("sprite") != canon["panel"]:
        paint = image_override(root_image, name + "-panel", copy_dir, copied, guid_cache, missing)
        if not any(rule.startswith("background-image") for rule in paint):
            # Missing sprite: uGUI draws the image color, not the shared panel.
            paint = ["background-image: none;", f"background-color: {rgba(root_image['color'])};"]
        add_delta(rules, f"#{name}", paint)
    record(node, parent_size, root_rules)
    for role, child in slots.items():
        rect = expected_rect(child, card_rect[2], card_rect[3])
        slot = canon["slots"][role]
        style = []
        if not close_rect(rect, slot["rect"]):
            style.extend(pixel_box(rect))
        image = child["behaviour"].get("image")
        if image and image.get("sprite") and image.get("sprite") != slot["sprite"]:
            style.extend(image_override(image, f"{name}-{role}", copy_dir, copied, guid_cache, missing))
        if text_paint(child) != slot["text"]:
            style.extend(text_paint(child))
        caption = label_of(child)
        add_delta(rules, f"#{name} #{role}", style)
        attrs = {}
        if role in ("title", "body") and caption and caption["behaviour"]["text"]["value"] != slot["value"]:
            attrs["text"] = caption["behaviour"]["text"]["value"]
        if attrs:
            overrides.append((role, attrs))
        record(child, (card_rect[2], card_rect[3]), pixel_box(rect))
        if role in ("primary", "secondary") and caption and caption is not child:
            button_rect = rect
            label_attrs = {}
            if caption["behaviour"]["text"]["value"] != slot["value"]:
                label_attrs["text"] = caption["behaviour"]["text"]["value"]
            if label_attrs:
                overrides.append((role + "Label", label_attrs))
            record(caption, (button_rect[2], button_rect[3]), [
                "position: absolute;", "left: 0;", "right: 0;", "top: 0;", "bottom: 0;",
            ])
    if "secondary" not in slots:
        add_delta(rules, f"#{name} #secondary", ["display: none;"])
    lines.extend(instance_lines("NoticeCard", name, join_style(root_rules), overrides, depth))
    return True


def emit_hud(node, form_name, used, lines, rules, depth, copy_dir, copied, guid_cache, missing, parent_name, parent_size, templates):
    templates.add("HudIcon")
    name = choose_name(node, form_name, used, parent_name)
    root_rules = layout_rules(node["rect"]) + opacity_paint(node)
    if not node["active"]:
        root_rules.append("display: none;")
    root_image = node["behaviour"].get("image")
    plate_sprite = CANON["HudIcon"]["plate"]
    if root_image and root_image.get("sprite") and root_image.get("sprite") != plate_sprite:
        root_rules.extend(image_override(root_image, name + "-plate", copy_dir, copied, guid_cache, missing))
    elif root_image and not root_image.get("sprite") and root_image["color"][3] > 0.01:
        root_rules.append(f"background-color: {rgba(root_image['color'])};")
    record(node, parent_size, root_rules)
    kind = node["name"]

    def show(element):
        add_delta(rules, f"#{name} #{element}", ["display: flex;"])

    def hide(element):
        add_delta(rules, f"#{name} #{element}", ["display: none;"])

    def glyph_sprite(child):
        image = child["behaviour"].get("image") if child else None
        if not image or not image.get("sprite") or image["sprite"] == CANON["HudIcon"]["glyph"]:
            return
        add_delta(rules, f"#{name} #glyph", image_override(image, name + "-glyph", copy_dir, copied, guid_cache, missing))

    if kind == "tianqidise":
        glyph_sprite(child_named(node, "tianqiicon"))
        record(child_named(node, "tianqidise"), (108, 108), pixel_box((0, 0, 108, 108)))
        record(child_named(node, "tianqiicon"), (108, 108), pixel_box((0, 0, 108, 108)))
    elif kind == "yinyuedise":
        glyph_sprite(child_named(node, "yinyueicon"))
        show("musicPlaying")
        record(child_named(node, "yinyuedise"), (108, 108), pixel_box((0, 0, 108, 108)))
        record(child_named(node, "yinyueicon"), (108, 108), pixel_box((0, 0, 108, 108)))
        pause = child_named(node, "yinyuePause")
        playing = child_named(node, "yinyueAnima")
        record(pause, (108, 108), pixel_box(expected_rect(pause, 108, 108)))
        record(playing, (108, 108), pixel_box(expected_rect(playing, 108, 108)))
        for bar in playing["children"]:
            if subtree_useful(bar):
                record(bar, (108, 108), pixel_box(expected_rect(bar, 108, 108)))
    elif kind == "tiaojiedise":
        hide("glyph")
        show("volumeMarks")
        record(child_named(node, "tiaojiedise"), (108, 108), pixel_box((0, 0, 108, 108)))
        marks = child_named(node, "tiaojie")
        record(marks, (108, 108), pixel_box(expected_rect(marks, 108, 108)))
        for mark in marks["children"]:
            if subtree_useful(mark):
                record(mark, (108, 108), pixel_box(expected_rect(mark, 108, 108)))
    elif kind == "wodeicon":
        glyph_sprite(child_named(node, "wodeicon"))
        show("badge")
        record(child_named(node, "wodeicon"), (108, 108), pixel_box((0, 0, 108, 108)))
        badge = child_named(node, "unread")
        record(badge, (108, 108), pixel_box(expected_rect(badge, 108, 108)))
    elif kind == "dingshidise":
        hide("glyph")
        show("timerActive")
        show("timerIdle")
        record(child_named(node, "dingshidise"), (108, 108), pixel_box((0, 0, 108, 108)))
        active = child_named(node, "dingshiyouren")
        idle = child_named(node, "dingshimoren")
        record(active, (108, 108), pixel_box(expected_rect(active, 108, 108)))
        record(idle, (108, 108), pixel_box(expected_rect(idle, 108, 108)))
        for part in active["children"]:
            if subtree_useful(part):
                record(part, (108, 108), pixel_box(expected_rect(part, 108, 108)))
    lines.extend(instance_lines("HudIcon", name, join_style(root_rules), [], depth))
    return True


def emit_level(node, form_name, used, lines, rules, depth, copy_dir, copied, guid_cache, missing, parent_name, parent_size, templates):
    templates.add("LevelKnob")
    name = choose_name(node, form_name, used, parent_name)
    root_rules = layout_rules(node["rect"]) + opacity_paint(node)
    if not node["active"]:
        root_rules.append("display: none;")
    record(node, parent_size, root_rules)
    if node["name"] == "drag":
        add_delta(rules, f"#{name} #highlight", ["display: flex;"])
        add_delta(rules, f"#{name} #label", ["display: flex;"])
        drag_rect = expected_rect(node, *parent_size)
        for child in node["children"]:
            if not subtree_useful(child):
                continue
            record(child, (drag_rect[2], drag_rect[3]), pixel_box(expected_rect(child, drag_rect[2], drag_rect[3])))
    lines.extend(instance_lines("LevelKnob", name, join_style(root_rules), [], depth))
    return True


def emit_volume_rows(root, form_name, used, guid_cache, missing, lines, rules, depth, copy_dir, copied, templates):
    groups = (
        ("musicVolume", "disetiao1", "yushedian1", "font11", "font1", "huangdongyanse1"),
        ("effectVolume", "disetiao2", "yushedian2", "font21", "font2", "huangdongyanse2"),
    )
    by_name = {child["name"]: child for child in root["children"]}
    if "disetiao1" not in by_name:
        return set()
    templates.add("VolumeRow")
    origin = CANON["VolumeRow"]["origin"]
    size = CANON["VolumeRow"]["size"]
    consumed = set()
    for instance, track_name, knob_name, value_name, caption_name, fill_name in groups:
        track = by_name[track_name]
        knob = by_name[knob_name]
        value = by_name[value_name]
        caption = by_name[caption_name]
        consumed.update({track["id"], knob["id"], value["id"], caption["id"]})
        left = min(expected_rect(part, *DESIGN)[0] for part in (track, knob, value, caption))
        top = min(expected_rect(part, *DESIGN)[1] for part in (track, knob, value, caption))
        box = (left, top, size[0], size[1])
        # The row is a generated parent. Its canvas position is the bounding box.
        root_rules = pixel_box(box)
        overrides = []
        local = CANON["VolumeRow"]["local"]
        for role, part in (("track", track), ("knob", knob), ("value", value), ("caption", caption)):
            part_rect = expected_rect(part, *DESIGN)
            part_local = (part_rect[0] - left, part_rect[1] - top, part_rect[2], part_rect[3])
            if not close_rect(part_local, local[role]):
                add_delta(rules, f"#{instance} #{role}", pixel_box(part_local))
            # Canvas-space check: row origin plus local box.
            record(part, DESIGN, pixel_box((left + part_local[0], top + part_local[1], part_local[2], part_local[3])))
        fill = child_named(track, fill_name)
        fill_rect = expected_rect(fill, part_rect_width(track), part_rect_height(track))
        if not close_rect(fill_rect, CANON["VolumeRow"]["fill"]):
            add_delta(rules, f"#{instance} #fill", pixel_box(fill_rect))
        record(fill, (part_rect_width(track), part_rect_height(track)), pixel_box(fill_rect))
        plate = child_named(track, track_name)
        if plate and subtree_useful(plate):
            plate_rect = expected_rect(plate, part_rect_width(track), part_rect_height(track))
            record(plate, (part_rect_width(track), part_rect_height(track)), pixel_box(plate_rect))
        face = knob["children"][0] if knob["children"] else None
        if face and subtree_useful(face):
            face_rect = expected_rect(face, part_rect_width(knob), part_rect_height(knob))
            record(face, (part_rect_width(knob), part_rect_height(knob)), pixel_box(face_rect))
        if value["behaviour"]["text"]["value"] != CANON["VolumeRow"]["value"]:
            overrides.append(("value", {"text": value["behaviour"]["text"]["value"]}))
        if caption["behaviour"]["text"]["value"] != CANON["VolumeRow"]["caption"]:
            overrides.append(("caption", {"text": caption["behaviour"]["text"]["value"]}))
        used.add(instance)
        lines.extend(instance_lines("VolumeRow", instance, join_style(root_rules), overrides, depth))
    return consumed


def part_rect_width(node):
    return expected_rect(node, *DESIGN)[2]


def part_rect_height(node):
    return expected_rect(node, *DESIGN)[3]


def emit(node, form_name, class_prefix, used, guid_cache, missing, lines, rules, depth, copy_dir, copied, parent_size, templates, parent_name=""):
    if template_of(node) == "LevelKnob" or subtree_useful(node):
        pass
    else:
        return
    if not subtree_useful(node) and template_of(node) != "LevelKnob":
        return
    if template_of(node) == "NoticeCard" and emit_notice(node, form_name, used, guid_cache, missing, lines, rules, depth, copy_dir, copied, parent_name, parent_size, templates):
        return
    if template_of(node) == "HudIcon" and emit_hud(node, form_name, used, lines, rules, depth, copy_dir, copied, guid_cache, missing, parent_name, parent_size, templates):
        return
    if template_of(node) == "LevelKnob" and emit_level(node, form_name, used, lines, rules, depth, copy_dir, copied, guid_cache, missing, parent_name, parent_size, templates):
        return
    if node["behaviour"].get("scroll"):
        # ListView replaces the scroll view and its spawned children.
        name = choose_name(node, form_name, used, parent_name)
        cls = f"{class_prefix}__{kebab(name)}"
        body = layout_rules(node["rect"])
        body.append("flex-grow: 1;")
        body.extend(opacity_paint(node))
        record(node, parent_size, body)
        rules.append(f".{cls} {{\n    " + "\n    ".join(body) + "\n}")
        pad = "    " * depth
        lines.append(f'{pad}<ui:ListView name="{name}" class="{cls}" />')
        return
    # Skip empty wrappers by promoting children, keeping the wrapper when it has a rect we need.
    # Keep wrappers that only exist to position children: they still affect layout.
    name = choose_name(node, form_name, used, parent_name)
    cls = f"{class_prefix}__{kebab(name)}"
    beh = node["behaviour"]
    body = layout_rules(node["rect"])
    if not node["active"]:
        body.append("display: none;")
    image = beh.get("image")
    text = beh.get("text")
    tag = "ui:VisualElement"
    if beh.get("button") is not None:
        tag = "ui:Button"
        body.append("padding: 0;")
        body.append("border-width: 0;")
        body.append("min-width: 0;")
        body.append("min-height: 0;")
        body.append("background-color: rgba(0, 0, 0, 0);")
    elif beh.get("slider") is not None:
        # Prefer VisualElement: Unity Slider brings default tracker/dragger chrome
        # that fights the authored track/fill sprites from uGUI.
        tag = "ui:VisualElement"
    elif text and not image:
        tag = "ui:Label"
    if image:
        rel, asset = copy_sprite(image.get("sprite"), name, copy_dir, copied, guid_cache, missing, FORMS_DIR)
        body.extend(image_paint(image, rel, asset))
        if not image["raycast"] and beh.get("button") is None and beh.get("slider") is None:
            body.append("picking-mode: Ignore;")
    body.extend(opacity_paint(node))
    if text:
        body.extend(text_paint(node))
    if tag == "ui:VisualElement" and not image and not text and beh.get("button") is None:
        body.append("picking-mode: Ignore;")
    record(node, parent_size, body)
    rules.append(f".{cls} {{\n    " + "\n    ".join(body) + "\n}")
    pad = "    " * depth
    text_value = ""
    if text and text["value"].strip() and tag == "ui:Label":
        text_value = escape(text["value"], {'"': "&quot;"})
    child_lines = []
    mine = expected_rect(node, *parent_size)
    child_parent = (
        mine[2] if abs(mine[2]) > 1 else parent_size[0],
        mine[3] if abs(mine[3]) > 1 else parent_size[1],
    )
    for child in node["children"]:
        emit(child, form_name, class_prefix, used, guid_cache, missing, child_lines, rules, depth + 1, copy_dir, copied, child_parent, templates, node["name"])
    if text_value and not child_lines:
        lines.append(f'{pad}<{tag} name="{name}" class="{cls}" text="{text_value}" />')
    elif not child_lines:
        lines.append(f'{pad}<{tag} name="{name}" class="{cls}" />')
    else:
        lines.append(f'{pad}<{tag} name="{name}" class="{cls}">')
        lines.extend(child_lines)
        lines.append(f"{pad}</{tag}>")


def write_form(form_name, roots, guid_cache, missing, item=False):
    class_prefix = "game-" + kebab(form_name)
    used = set()
    lines = []
    rules = []
    copy_dir = os.path.join(OUT, "Textures", form_name)
    copied = {}
    # Prefer the largest root that has content.
    useful = [root for root in roots if subtree_useful(root)]
    if not useful:
        print("skip empty", form_name)
        return
    root = max(useful, key=lambda n: len(n["children"]))
    # The prefab root is the full canvas. Emit its children into a stretch root so
    # the document itself does not add a second full-screen offset.
    inner = []
    inner_rules = []
    templates = set()
    root_size = root["rect"]["size"]
    parent_size = (
        root_size[0] if abs(root_size[0]) > 1 else DESIGN[0],
        root_size[1] if abs(root_size[1]) > 1 else DESIGN[1],
    )
    consumed = set()
    if form_name == "VolumeForm":
        consumed = emit_volume_rows(root, form_name, used, guid_cache, missing, inner, inner_rules, 2, copy_dir, copied, templates)
    for child in root["children"]:
        if child["id"] in consumed:
            continue
        emit(child, form_name, class_prefix, used, guid_cache, missing, inner, inner_rules, 2, copy_dir, copied, parent_size, templates, root["name"])
    # Also emit root image if it is a visible backdrop.
    root_bits = []
    if has_content(root):
        emit_root_backdrop(root, form_name, class_prefix, used, guid_cache, missing, root_bits, inner_rules, copy_dir, copied, parent_size, templates)
    header = [
        '<ui:UXML xmlns:ui="UnityEngine.UIElements">',
        f'    <Style src="{form_name}.uss" />',
    ]
    for template in ("NoticeCard", "HudIcon", "VolumeRow", "LevelKnob"):
        if template in templates:
            header.append(f'    <ui:Template name="{template}" src="../Components/{template}.uxml" />')
    header.append(f'    <ui:VisualElement name="root" class="{class_prefix}__root">')
    header.extend(root_bits)
    header.extend(inner)
    header.append("    </ui:VisualElement>")
    header.append("</ui:UXML>")
    if item:
        width, height = root["rect"]["size"]
        uss = [
            f".{class_prefix}__root {{",
            "    position: relative;",
            f"    width: {px(abs(width) if abs(width) > 1 else 900)};",
            f"    height: {px(abs(height) if abs(height) > 1 else 220)};",
            "}",
            "",
        ]
    else:
        uss = [
            f".{class_prefix}__root {{",
            "    position: absolute;",
            "    left: 0;",
            "    right: 0;",
            "    top: 0;",
            "    bottom: 0;",
            "}",
            "",
        ]
    uss.extend(inner_rules)
    os.makedirs(os.path.join(OUT, "Forms"), exist_ok=True)
    with open(os.path.join(OUT, "Forms", form_name + ".uxml"), "w") as handle:
        handle.write("\n".join(header) + "\n")
    with open(os.path.join(OUT, "Forms", form_name + ".uss"), "w") as handle:
        handle.write("\n".join(uss) + "\n")
    print(form_name, "elements", len(used), "textures", len(copied))


def emit_root_backdrop(node, form_name, class_prefix, used, guid_cache, missing, lines, rules, copy_dir, copied, parent_size, templates):
    # Reuse emit but the root rect is stretch-full; children already emitted separately.
    clone = dict(node)
    clone["children"] = []
    emit(clone, form_name, class_prefix, used, guid_cache, missing, lines, rules, 2, copy_dir, copied, parent_size, templates)


def add_loading_video(path):
    text = open(path).read()
    if 'name="campfireStill"' in text:
        return
    # Paint above the BankUpdate layers so the game loading screen is visible.
    block = """        <ui:VisualElement name="campfireStill" class="game-loading-form__campfire-still" />
        <ui:VisualElement name="campfireVideo" class="game-loading-form__campfire-video" picking-mode="Ignore" />
"""
    text = text.replace(
        "    </ui:VisualElement>\n</ui:UXML>",
        block + "    </ui:VisualElement>\n</ui:UXML>",
        1,
    )
    open(path, "w").write(text)
    uss_path = path.replace(".uxml", ".uss")
    extra = """
.game-loading-form__campfire-still,
.game-loading-form__campfire-video {
    position: absolute;
    left: 0;
    right: 0;
    top: 0;
    bottom: 0;
    -unity-background-scale-mode: scale-and-crop;
}
.game-loading-form__campfire-video {
    picking-mode: Ignore;
}
"""
    with open(uss_path, "a") as handle:
        handle.write(extra)


def main():
    os.makedirs(os.path.join(OUT, "Fonts"), exist_ok=True)
    font_src = os.path.join(SRC, "Assets/Arts/Font/pingfangsc-medium.ttf")
    shutil.copy2(font_src, os.path.join(OUT, "Fonts/pingfangsc-medium.ttf"))
    video_dir = os.path.join(OUT, "Video")
    os.makedirs(video_dir, exist_ok=True)
    for fn in ("loading.mp4", "loading_landscape.mp4", "loading.png", "loading_landscape.png"):
        shutil.copy2(os.path.join(SRC, "Assets/Arts/BankUpdate", fn), os.path.join(video_dir, fn))
    guid_cache = index_guids()
    missing = {}
    build_templates(guid_cache, missing)
    for src_name, form_name in {**FORMS, **ITEMS}.items():
        folder = "View" if src_name in FORMS else "UIItem"
        path = os.path.join(PREFAB_DIR, folder, src_name + ".prefab")
        roots, _ = parse_unity(path)
        write_form(form_name, roots, guid_cache, missing, item=src_name in ITEMS)
    roots, nodes = parse_unity(os.path.join(SRC, "Assets/Empty.unity"))
    loading_roots = []
    for node in nodes.values():
        if node["behaviour"].get("loading"):
            current = node
            while current["father"] in nodes:
                current = nodes[current["father"]]
            loading_roots = [current]
            break
    if loading_roots:
        write_form("LoadingForm", loading_roots, guid_cache, missing)
        add_loading_video(os.path.join(OUT, "Forms/LoadingForm.uxml"))
    else:
        print("loading root not found")
    verify_records()


def verify_records():
    bad = []
    seen = set()
    for item in RECORDS:
        seen.add(item["id"])
        resolved = resolve_style(item["style"], *item["parent"])
        if not close_rect(resolved, item["expected"]):
            bad.append((item["name"], item["expected"], resolved))
    missing = []

    def walk(node, under_scroll=False):
        scroll = under_scroll or bool(node["behaviour"].get("scroll"))
        if not under_scroll and subtree_useful(node) and node["id"] not in seen and not node["behaviour"].get("scroll"):
            # The form root itself is the stretch host, not an emitted element.
            if node["father"] != "0":
                missing.append(node["name"])
        for child in node["children"]:
            walk(child, scroll)

    for src_name, form_name in {**FORMS, **ITEMS}.items():
        folder = "View" if src_name in FORMS else "UIItem"
        roots, _ = parse_unity(os.path.join(PREFAB_DIR, folder, src_name + ".prefab"))
        useful = [root for root in roots if subtree_useful(root)]
        if useful:
            walk(max(useful, key=lambda n: len(n["children"])))
    _, nodes = parse_unity(os.path.join(SRC, "Assets/Empty.unity"))
    for node in nodes.values():
        if node["behaviour"].get("loading"):
            current = node
            while current["father"] in nodes:
                current = nodes[current["father"]]
            walk(current)
            break
    print(f"rect check: {len(RECORDS)} nodes, {len(bad)} mismatches, {len(missing)} unplaced")
    for name, expected, resolved in bad[:20]:
        print(f"  mismatch {name}: expected {tuple(round(v, 1) for v in expected)} resolved {tuple(round(v, 1) for v in resolved)}")
    if missing:
        print("  unplaced", ", ".join(missing[:40]))
    if bad or missing:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
