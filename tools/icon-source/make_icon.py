# 生成 LocalTransfer 应用图标：
#   - src/LocalTransfer.Windows/App.ico   （WPF：exe 内嵌图标 + 标题栏 + 托盘）
#   - 自检截图/icon-preview-512.png        （预览）
# 设计：蓝色渐变圆角底 + 白色手机/笔记本轮廓 + 中间一右一左两支箭头（双向传输）。
# 用 2048 超采样绘制后缩小，保证小尺寸下的抗锯齿质量。
from PIL import Image, ImageDraw

S = 2048            # 超采样画布
TILE = 1024         # 最终画布逻辑尺寸（S = TILE * 2）

TOP = (74, 134, 255)     # #4A86FF
BOTTOM = (36, 80, 214)   # #2450D6

def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m

# 1) 渐变底 + 圆角
gradient = Image.new("RGB", (S, S))
top_row = TOP
bottom_row = BOTTOM
px = gradient.load()
for y in range(S):
    t = y / (S - 1)
    r = round(top_row[0] + (bottom_row[0] - top_row[0]) * t)
    g = round(top_row[1] + (bottom_row[1] - top_row[1]) * t)
    b = round(top_row[2] + (bottom_row[2] - top_row[2]) * t)
    for x in range(S):
        px[x, y] = (r, g, b)
mask = rounded_mask(S, int(440))  # 1024 逻辑下 220
tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
tile.paste(gradient, (0, 0), mask)

draw = ImageDraw.Draw(tile)
WHITE = (255, 255, 255, 255)

def rrect(box, radius, outline=None, width=0, fill=None):
    draw.rounded_rectangle(box, radius=radius, outline=outline, width=width, fill=fill)

# 2) 手机（左，竖屏轮廓）
rrect([340, 600, 760, 1560], radius=100, outline=WHITE, width=76)
# 3) 笔记本（右：屏幕轮廓 + 底座横条）
rrect([1240, 660, 1840, 1280], radius=72, outline=WHITE, width=76)
rrect([1160, 1320, 1920, 1400], radius=40, fill=WHITE)
# 4) 双向箭头（中间）：上箭头向右，下箭头向左
shaft_y1, shaft_y2 = 880, 944
draw.rectangle([820, shaft_y1, 1130, shaft_y2], fill=WHITE)
draw.polygon([(1130, 820), (1130, 1004), (1270, 912)], fill=WHITE)
shaft2_y1, shaft2_y2 = 1104, 1168
draw.rectangle([910, shaft2_y1, 1220, shaft2_y2], fill=WHITE)
draw.polygon([(910, 1092), (910, 1180), (770, 1136)], fill=WHITE)

# 5) 输出：ICO 多尺寸 + 预览 PNG
final = tile.resize((TILE, TILE), Image.LANCZOS)
final.save(
    r"D:\自我提升\AI实现\LocalTransfer\src\LocalTransfer.Windows\App.ico",
    format="ICO",
    sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)],
)
final.resize((512, 512), Image.LANCZOS).save(
    r"D:\自我提升\AI实现\LocalTransfer\自检截图\icon-preview-512.png", format="PNG"
)
print("ICON-OK")
