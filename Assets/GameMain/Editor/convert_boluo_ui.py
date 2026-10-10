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
    "InGameMobileDataView": "MobileDataForm",
    "InGameRequestDownloadView": "DownloadAskForm",
    "InGameNeedNetWorkView": "NetworkRequiredForm",
    "InGameDownloadView": "DownloadProgressForm",
    "InGameInsufficientStorageView": "StorageFullForm",
}

ITEMS = {
    "Musicitem": "MusicTrackItem",
    "YusheItem": "PresetItem",
    "state_Playing": "PlayingBadge",
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
                info["image"] = {
                    "color": tuple(float(x) for x in color.groups()) if color else (1, 1, 1, 1),
                    "sprite": sprite.group(1) if sprite else None,
                    "raycast": ray.group(1) == "1" if ray else False,
                }
            elif guid == BUTTON_GUID:
                method = re.search(r"m_MethodName: (\w+)", doc)
                info["button"] = method.group(1) if method else ""
            elif guid in (TMP_GUID, TEXT_GUID):
                text_match = re.search(r"\n\s*m_[Tt]ext: (.*)", doc)
                size = re.search(r"\n\s*m_[Ff]ontSize: ([-\d.]+)", doc)
                color = re.search(r"m_fontColor: \{r: ([-\d.]+), g: ([-\d.]+), b: ([-\d.]+), a: ([-\d.]+)", doc)
                if not color:
                    color = re.search(r"m_Color: \{r: ([-\d.]+), g: ([-\d.]+), b: ([-\d.]+), a: ([-\d.]+)", doc)
                halign = re.search(r"\n\s*m_HorizontalAlignment: (\d+)", doc)
                valign = re.search(r"\n\s*m_VerticalAlignment: (\d+)", doc)
                legacy = re.search(r"\n\s*m_Alignment: (\d+)", doc)
                info["text"] = {
                    "value": decode_yaml_string(text_match.group(1)) if text_match else "",
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


def emit(node, form_name, class_prefix, used, guid_cache, missing, lines, rules, depth, copy_dir, copied, parent_name=""):
    if not subtree_useful(node):
        return
    if node["behaviour"].get("scroll"):
        # ListView replaces the scroll view and its spawned children.
        name = choose_name(node, form_name, used, parent_name)
        cls = f"{class_prefix}__{kebab(name)}"
        body = layout_rules(node["rect"])
        body.append("flex-grow: 1;")
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
        tag = "ui:Slider"
    elif text and not image:
        tag = "ui:Label"
    if image:
        if image["sprite"]:
            asset = lookup_guid(image["sprite"], guid_cache, missing)
            if asset:
                ext = os.path.splitext(asset)[1]
                dest_name = kebab(name) + ext
                dest = os.path.join(copy_dir, dest_name)
                # Avoid collisions inside one form folder.
                if dest_name in copied and copied[dest_name] != asset:
                    dest_name = kebab(name) + "-" + image["sprite"][:6] + ext
                    dest = os.path.join(copy_dir, dest_name)
                if dest_name not in copied:
                    os.makedirs(copy_dir, exist_ok=True)
                    shutil.copy2(asset, dest)
                    meta = asset + ".meta"
                    if os.path.isfile(meta):
                        shutil.copy2(meta, dest + ".meta")
                    copied[dest_name] = asset
                rel = os.path.relpath(dest, os.path.join(OUT, "Forms")).replace(os.sep, "/")
                body.append(f'background-image: url("{rel}");')
                body.append("-unity-background-scale-mode: stretch-to-fill;")
                body.append(f"-unity-background-image-tint-color: {rgba(image['color'])};")
            else:
                body.append(f"background-color: {rgba(image['color'])};")
        else:
            body.append(f"background-color: {rgba(image['color'])};")
        if not image["raycast"] and beh.get("button") is None and beh.get("slider") is None:
            body.append("picking-mode: Ignore;")
    if text:
        unity_align, _, _ = align_text(text)
        body.append(f"-unity-text-align: {unity_align};")
        body.append(f"font-size: {text['size']:.0f}px;")
        body.append(f"color: {rgba(text['color'])};")
        point = abs(node["rect"]["size"][0]) <= 0.5 and abs(node["rect"]["size"][1]) <= 0.5
        body.append("white-space: nowrap;" if point else "white-space: normal;")
        if point:
            body.append("translate: -50% -50%;")
        body.append("-unity-font: url(\"../Fonts/pingfangsc-medium.ttf\");")
    if tag == "ui:VisualElement" and not image and not text and beh.get("button") is None:
        body.append("picking-mode: Ignore;")
    rules.append(f".{cls} {{\n    " + "\n    ".join(body) + "\n}")
    pad = "    " * depth
    text_value = ""
    if text and text["value"].strip() and tag == "ui:Label":
        text_value = escape(text["value"], {'"': "&quot;"})
    child_lines = []
    for child in node["children"]:
        emit(child, form_name, class_prefix, used, guid_cache, missing, child_lines, rules, depth + 1, copy_dir, copied, node["name"])
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
    for child in root["children"]:
        emit(child, form_name, class_prefix, used, guid_cache, missing, inner, inner_rules, 2, copy_dir, copied, root["name"])
    # Also emit root image if it is a visible backdrop.
    root_bits = []
    if has_content(root):
        emit_root_backdrop(root, form_name, class_prefix, used, guid_cache, missing, root_bits, inner_rules, copy_dir, copied)
    header = [
        '<ui:UXML xmlns:ui="UnityEngine.UIElements">',
        f'    <Style src="{form_name}.uss" />',
        f'    <ui:VisualElement name="root" class="{class_prefix}__root">',
    ]
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


def emit_root_backdrop(node, form_name, class_prefix, used, guid_cache, missing, lines, rules, copy_dir, copied):
    # Reuse emit but the root rect is stretch-full; children already emitted separately.
    clone = dict(node)
    clone["children"] = []
    emit(clone, form_name, class_prefix, used, guid_cache, missing, lines, rules, 2, copy_dir, copied)


def add_loading_video(path):
    text = open(path).read()
    if 'name="campfireStill"' in text:
        return
    block = """        <ui:VisualElement name="campfireStill" class="game-loading-form__campfire-still" />
        <ui:VisualElement name="campfireVideo" class="game-loading-form__campfire-video" picking-mode="Ignore" />
        <ui:Button name="enterIslandButton" class="game-loading-form__enter-island-button" text="进入岛屿" />
"""
    text = text.replace(
        '<ui:VisualElement name="root" class="game-loading-form__root">',
        '<ui:VisualElement name="root" class="game-loading-form__root">\n' + block,
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
.game-loading-form__enter-island-button {
    position: absolute;
    left: 50%;
    bottom: 180px;
    width: 420px;
    height: 120px;
    translate: -50% 0;
    font-size: 42px;
    color: rgb(255, 255, 255);
    background-color: rgba(0, 0, 0, 0.45);
    border-width: 0;
    -unity-font: url("../Fonts/pingfangsc-medium.ttf");
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


if __name__ == "__main__":
    main()
