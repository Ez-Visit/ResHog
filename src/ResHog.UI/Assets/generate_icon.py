from PIL import Image, ImageDraw
import math
import os

assets_dir = r"D:\ProgramData\WorkBuddyData\2026-07-06-22-43-39\ResHog\src\ResHog.UI\Assets"

# Theme colors
TEAL = (13, 115, 119, 255)
TEAL_DARK = (10, 80, 83, 255)
TEAL_LIGHT = (20, 145, 150, 255)
ORANGE = (216, 90, 48, 255)
ORANGE_DARK = (180, 70, 35, 255)
WHITE = (255, 255, 255, 255)
OFF_WHITE = (245, 245, 245, 255)
BLACK = (40, 40, 40, 255)


def draw_reshog_icon(size):
    """Draw a clean ResHog app icon at the given size."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    s = size / 256.0
    cx = size // 2
    cy = size // 2
    outer_r = int(120 * s)
    inner_r = int(52 * s)

    # --- Background disc ---
    draw.ellipse([cx - outer_r, cy - outer_r, cx + outer_r, cy + outer_r], fill=ORANGE)

    # --- Left semicircle (teal) ---
    left_mask = Image.new("L", img.size, 0)
    md = ImageDraw.Draw(left_mask)
    md.pieslice([cx - outer_r, cy - outer_r, cx + outer_r, cy + outer_r], start=90, end=270, fill=255)
    teal_layer = Image.new("RGBA", img.size, TEAL)
    img = Image.composite(teal_layer, img, left_mask)
    draw = ImageDraw.Draw(img)

    # --- Gauge tick marks on right half (white arcs) ---
    tick_angles = [-75, -55, -35, -15, 5, 25, 45, 65]
    tick_outer = outer_r - int(10 * s)
    tick_inner = inner_r + int(6 * s)
    for angle in tick_angles:
        start = angle - 2
        end = angle + 2
        draw.pieslice([cx - tick_outer, cy - tick_outer,
                       cx + tick_outer, cy + tick_outer],
                      start=start, end=end, fill=OFF_WHITE)
        mask = Image.new("L", img.size, 0)
        md = ImageDraw.Draw(mask)
        md.pieslice([cx - tick_inner, cy - tick_inner,
                     cx + tick_inner, cy + tick_inner],
                    start=start, end=end, fill=255)
        inner_layer = Image.new("RGBA", img.size, ORANGE)
        img = Image.composite(inner_layer, img, mask)
        draw = ImageDraw.Draw(img)

    # --- Inner circular gauge background (lighter orange) ---
    draw.ellipse([cx - inner_r, cy - inner_r, cx + inner_r, cy + inner_r], fill=(235, 115, 70, 255))

    # --- Hog head silhouette (left half) ---
    head_r = int(70 * s)
    head_cx = cx - int(16 * s)
    head_cy = cy
    draw.ellipse([head_cx - head_r, head_cy - head_r, head_cx + head_r, head_cy + head_r], fill=TEAL)

    # Snout
    snout_rx = int(34 * s)
    snout_ry = int(26 * s)
    snout_cx = head_cx - int(52 * s)
    snout_cy = head_cy + int(10 * s)
    draw.ellipse([snout_cx - snout_rx, snout_cy - snout_ry,
                  snout_cx + snout_rx, snout_cy + snout_ry], fill=TEAL_DARK)

    # Nostrils
    nos_r = max(1, int(4 * s))
    draw.ellipse([snout_cx - int(10*s) - nos_r, snout_cy - nos_r,
                  snout_cx - int(10*s) + nos_r, snout_cy + nos_r], fill=BLACK)
    draw.ellipse([snout_cx + int(10*s) - nos_r, snout_cy - nos_r,
                  snout_cx + int(10*s) + nos_r, snout_cy + nos_r], fill=BLACK)

    # Eye
    eye_r = max(2, int(7 * s))
    eye_cx = head_cx - int(8 * s)
    eye_cy = head_cy - int(22 * s)
    draw.ellipse([eye_cx - eye_r, eye_cy - eye_r, eye_cx + eye_r, eye_cy + eye_r], fill=ORANGE)

    # Ear (pointed triangle)
    ear_points = [
        (head_cx + int(30*s), head_cy - int(52*s)),
        (head_cx + int(58*s), head_cy - int(82*s)),
        (head_cx + int(2*s), head_cy - int(80*s))
    ]
    draw.polygon(ear_points, fill=TEAL_DARK)
    # Inner ear highlight
    ear_inner = [
        (head_cx + int(32*s), head_cy - int(55*s)),
        (head_cx + int(52*s), head_cy - int(75*s)),
        (head_cx + int(14*s), head_cy - int(74*s))
    ]
    draw.polygon(ear_inner, fill=TEAL_LIGHT)

    # --- Vertical divider line ---
    line_w = max(1, int(3 * s))
    draw.line([(cx, cy - outer_r), (cx, cy + outer_r)], fill=WHITE, width=line_w)

    # --- Center pivot ---
    pivot_r = max(2, int(9 * s))
    draw.ellipse([cx - pivot_r, cy - pivot_r, cx + pivot_r, cy + pivot_r], fill=WHITE)

    # --- Gauge needle (white, pointing at ~55 degrees) ---
    needle_len = int(44 * s)
    needle_w = max(1, int(5 * s))
    angle = math.radians(55)
    tip = (cx + int(needle_len * math.cos(angle)), cy - int(needle_len * math.sin(angle)))
    perp = (int(needle_w * math.sin(angle)), int(needle_w * math.cos(angle)))
    needle_points = [
        (cx - perp[0], cy + perp[1]),
        (cx + perp[0], cy - perp[1]),
        tip
    ]
    draw.polygon(needle_points, fill=WHITE)

    return img


def main():
    sizes = [16, 32, 48, 64, 128, 256]
    images = {size: draw_reshog_icon(size) for size in sizes}

    # Save PNG assets
    images[256].save(os.path.join(assets_dir, "app.png"), "PNG")
    images[32].save(os.path.join(assets_dir, "tray.png"), "PNG")
    images[16].save(os.path.join(assets_dir, "favicon.png"), "PNG")

    # Save ICO (single largest size; Windows scales as needed)
    ico_path = os.path.join(assets_dir, "app.ico")
    images[256].save(ico_path, format="ICO", sizes=[(256, 256)])

    print(f"Generated icons in {assets_dir}:")
    for f in ["app.png", "tray.png", "favicon.png", "app.ico"]:
        print(f"  {f}: {os.path.getsize(os.path.join(assets_dir, f))} bytes")


if __name__ == "__main__":
    main()
