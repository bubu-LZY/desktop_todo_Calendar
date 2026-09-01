from PIL import Image, ImageDraw
import os

def create_task_icon(size=256):
    # 创建透明背景的图像
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # 颜色
    bg_color = (70, 130, 180)  # 钢蓝色
    dot_color = (255, 255, 255)  # 白色
    line_color = (255, 255, 255)  # 白色

    # 画圆角矩形背景
    margin = int(size * 0.1)
    draw.rounded_rectangle(
        [margin, margin, size - margin, size - margin],
        radius=int(size * 0.15),
        fill=bg_color
    )

    # 三行的Y位置
    line1_y = int(size * 0.35)
    line2_y = int(size * 0.5)
    line3_y = int(size * 0.65)

    # 左侧圆点X位置
    dot_x = int(size * 0.25)
    # 圆点大小
    dot_size = int(size * 0.06)

    # 右侧横线起始X位置
    line_start_x = int(size * 0.38)
    # 右侧横线结束X位置
    line_end_x = int(size * 0.78)
    # 横线粗细
    line_thickness = int(size * 0.025)

    # 画三个圆点
    for y in [line1_y, line2_y, line3_y]:
        draw.ellipse(
            [dot_x - dot_size, y - dot_size, dot_x + dot_size, y + dot_size],
            fill=dot_color
        )

    # 画三条横线
    for y in [line1_y, line2_y, line3_y]:
        draw.rectangle(
            [line_start_x, y - line_thickness, line_end_x, y + line_thickness],
            fill=line_color
        )

    return img

# 创建不同尺寸的图标
output_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "MicaAgenda.App", "Assets")
os.makedirs(output_dir, exist_ok=True)

sizes = [16, 32, 48, 64, 128, 256]
for size in sizes:
    icon = create_task_icon(size)
    output_path = os.path.join(output_dir, f"task-icon-{size}.png")
    icon.save(output_path)
    print(f"Created: {output_path}")

# 创建ICO文件（包含多个尺寸）
ico_sizes = [16, 32, 48, 64]
ico_images = []
for size in ico_sizes:
    ico_images.append(create_task_icon(size))

ico_path = os.path.join(output_dir, "app.ico")
ico_images[0].save(
    ico_path,
    format='ICO',
    sizes=[(s, s) for s in ico_sizes],
    append_images=ico_images[1:]
)
print(f"Created ICO: {ico_path}")
