from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[2]
GUIDES = ROOT / "Assets" / "Creator" / "Guides"
CELL = (40, 56)

BG = (24, 30, 47, 255)
PANEL = (38, 47, 68, 255)
TEXT = (244, 246, 252, 255)
MUTED = (181, 190, 207, 255)


def font(size: int, bold: bool = False):
    names = ["arialbd.ttf" if bold else "arial.ttf", "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"]
    for name in names:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


TITLE = font(24, True)
LABEL = font(14, True)
SMALL = font(12)


def open_png_bytes(path: Path) -> Image.Image:
    with Image.open(path) as image:
        return image.convert("RGBA")


def checker(size):
    image = Image.new("RGBA", size, (48, 52, 63, 255))
    draw = ImageDraw.Draw(image)
    block = 12
    for y in range(0, size[1], block):
        for x in range(0, size[0], block):
            if (x // block + y // block) % 2:
                draw.rectangle((x, y, x + block - 1, y + block - 1), fill=(68, 73, 87, 255))
    return image


def render_runtime_map(source_name, output_name, title, labels, colors):
    source = open_png_bytes(GUIDES / source_name)
    scale = 3
    image_size = (CELL[0] * scale, CELL[1] * scale)
    tile_size = (image_size[0] + 16, image_size[1] + 48)
    columns, rows = 5, 4
    top = 58
    canvas = Image.new("RGBA", (columns * tile_size[0] + 16, top + rows * tile_size[1] + 16), BG)
    draw = ImageDraw.Draw(canvas)
    draw.text((16, 12), title, fill=TEXT, font=TITLE)
    draw.text((16, 38), "Cell index and runtime state (guide only)", fill=MUTED, font=SMALL)

    for index in range(20):
        col, row = index % columns, index // columns
        x = 8 + col * tile_size[0]
        y = top + row * tile_size[1]
        color = colors[index]
        draw.rounded_rectangle((x, y, x + tile_size[0] - 8, y + tile_size[1] - 8), 5, fill=PANEL, outline=color, width=3)
        draw.text((x + 8, y + 6), f"{index:02}  {labels[index]}", fill=color, font=LABEL)
        frame = source.crop((0, index * CELL[1], CELL[0], (index + 1) * CELL[1]))
        frame = frame.resize(image_size, Image.Resampling.NEAREST)
        preview = checker(image_size)
        preview.alpha_composite(frame)
        canvas.alpha_composite(preview, (x + 8, y + 32))

    canvas.save(GUIDES / output_name, format="PNG", optimize=True)


def body_label(index):
    fixed = {
        0: "MALE TORSO", 1: "MALE JUMP", 7: "FR WPN FULL", 8: "BK WPN FULL",
        9: "M FR SH", 10: "M BK SH", 16: "FR WPN 3/4", 17: "BK WPN 3/4",
        18: "FEM TORSO", 19: "FEM JUMP", 25: "FR WPN 1/4", 26: "BK WPN 1/4",
        27: "F FR SH", 28: "F BK SH", 34: "FR WPN NONE", 35: "BK WPN NONE"
    }
    if index in fixed:
        return fixed[index]
    if 2 <= index <= 6:
        return f"FR ARM {index - 2:02}"
    if 11 <= index <= 15:
        return f"FR ARM {index - 6:02}"
    if 20 <= index <= 24:
        return f"BK ARM {index - 20:02}"
    return f"BK ARM {index - 24:02}"


def body_color(index):
    if index in (0, 1, 18, 19):
        return (205, 139, 255, 255)
    if index in (9, 10, 27, 28):
        return (70, 220, 211, 255)
    if index in (7, 16, 25, 34):
        return (255, 173, 71, 255)
    if index in (8, 17, 26, 35):
        return (255, 105, 105, 255)
    if 2 <= index <= 15:
        return (102, 224, 138, 255)
    return (104, 174, 255, 255)


def render_body_map():
    source = open_png_bytes(GUIDES / "body-guide.bin")
    scale = 2
    image_size = (CELL[0] * scale, CELL[1] * scale)
    tile_size = (image_size[0] + 10, image_size[1] + 42)
    columns, rows = 9, 4
    top = 76
    canvas = Image.new("RGBA", (columns * tile_size[0] + 16, top + rows * tile_size[1] + 16), BG)
    draw = ImageDraw.Draw(canvas)
    draw.text((16, 10), "BODY COMPOSITE CELL MAP", fill=TEXT, font=TITLE)
    draw.text((16, 38), "FR/BK = front/back; WPN = explicit weapon arm; M/F = male/female", fill=MUTED, font=SMALL)
    draw.text((16, 55), "These are parts, not 36 whole-character animation frames.", fill=(255, 208, 104, 255), font=SMALL)

    for index in range(36):
        col, row = index % columns, index // columns
        x = 8 + col * tile_size[0]
        y = top + row * tile_size[1]
        color = body_color(index)
        draw.rectangle((x, y, x + tile_size[0] - 4, y + tile_size[1] - 6), fill=PANEL, outline=color, width=2)
        draw.text((x + 5, y + 4), f"{index:02}", fill=TEXT, font=LABEL)
        draw.text((x + 5, y + 20), body_label(index), fill=color, font=SMALL)
        frame = source.crop((col * CELL[0], row * CELL[1], (col + 1) * CELL[0], (row + 1) * CELL[1]))
        frame = frame.resize(image_size, Image.Resampling.NEAREST)
        preview = checker(image_size)
        preview.alpha_composite(frame)
        canvas.alpha_composite(preview, (x + 5, y + 38))

    canvas.save(GUIDES / "body-state-map.bin", format="PNG", optimize=True)


def main():
    idle = (112, 225, 153, 255)
    action = (255, 192, 89, 255)
    air = (177, 135, 255, 255)
    move = (91, 182, 255, 255)
    special = (181, 190, 207, 255)

    head_labels = ["IDLE"] + [f"ACTION {i}" for i in range(1, 5)] + ["JUMP", "AIR"] + [f"MOVE {i:02}" for i in range(1, 14)]
    head_colors = [idle] + [action] * 4 + [air] * 2 + [move] * 13
    render_runtime_map("head-guide.bin", "head-state-map.bin", "HEAD RUNTIME FRAME MAP", head_labels, head_colors)

    legs_labels = ["IDLE / USE", "SPECIAL", "SPECIAL", "SPECIAL", "SPECIAL", "JUMP", "TRANSITION"] + [f"MOVE {i:02}" for i in range(1, 14)]
    legs_colors = [idle] + [special] * 4 + [air, special] + [move] * 13
    render_runtime_map("legs-guide.bin", "legs-state-map.bin", "LEGS RUNTIME FRAME MAP", legs_labels, legs_colors)
    render_body_map()


if __name__ == "__main__":
    main()
