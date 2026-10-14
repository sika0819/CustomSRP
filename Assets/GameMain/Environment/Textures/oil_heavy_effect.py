#!/usr/bin/env python3
"""
厚涂油画风格生成器 v2 - 增强版
使用 Kuwahara 滤镜 + 方向性笔触 + 油画纹理增强
"""

import numpy as np
from PIL import Image, ImageFilter, ImageEnhance, ImageDraw
import os
import math

def apply_brush_strokes(img, stroke_length=15, stroke_strength=0.4):
    """
    添加方向性笔触效果 - 模拟真实油画的笔刷痕迹
    """
    arr = np.array(img)
    height, width = arr.shape[:2]
    output = arr.copy()
    
    # 沿多个方向添加笔触
    directions = [0, 45, 90, 135]
    
    for angle in directions:
        rad = math.radians(angle)
        dx = int(stroke_length * math.cos(rad))
        dy = int(stroke_length * math.sin(rad))
        
        for y in range(0, height, stroke_length // 2):
            for x in range(0, width, stroke_length // 2):
                # 采样该点的颜色
                ny, nx = y + dy, x + dx
                if 0 <= ny < height and 0 <= nx < width:
                    # 获取原始颜色
                    color = arr[y, x].astype(float)
                    # 混合到笔触末端
                    target = arr[ny, nx].astype(float)
                    blended = color * (1 - stroke_strength) + target * stroke_strength
                    output[y, x] = blended
    
    return Image.fromarray(output.astype(np.uint8))


def oil_texture_enhance(img):
    """
    油画纹理增强 - 增加厚涂的颗粒感和层次感
    """
    result = img.copy()
    
    # 1. 多次不同强度的模糊叠加 - 模拟厚涂的柔和边缘
    for i, radius in enumerate([2, 4, 6]):
        blurred = result.filter(ImageFilter.GaussianBlur(radius))
        result = Image.blend(result, blurred, 0.25 - i * 0.05)
    
    # 2. 边缘检测后混合 - 保留笔触边界
    edges = result.filter(ImageFilter.FIND_EDGES)
    edges = ImageEnhance.Contrast(edges).enhance(0.5)
    result = ImageChop_add(result, edges, 0.3)
    
    # 3. 细节增强
    result = result.filter(ImageFilter.DETAIL)
    result = result.filter(ImageFilter.SHARPEN)
    
    return result


def ImageChop_add(img1, img2, alpha):
    """将两张图按alpha混合"""
    img1_arr = np.array(img1).astype(float)
    img2_arr = np.array(img2).astype(float)
    blended = img1_arr * (1 - alpha) + img2_arr * alpha
    return Image.fromarray(np.clip(blended, 0, 255).astype(np.uint8))


def kuwahara_heavy(img, radius=5):
    """
    厚重版 Kuwahara 滤镜 - 真正的油画算法
    区域越小越细腻，区域越大笔触越粗
    """
    arr = np.array(img)
    if len(arr.shape) == 2:
        arr = np.stack([arr, arr, arr], axis=-1)
    
    height, width, channels = arr.shape
    output = np.zeros_like(arr, dtype=np.float64)
    
    for c in range(min(channels, 3)):
        channel = arr[:, :, c].astype(np.float64)
        
        for y in range(radius, height - radius):
            for x in range(radius, width - radius):
                # 4个重叠扇形区域 (带偏移)
                regions = [
                    channel[y-radius:y+1, x-radius:x+1],
                    channel[y-radius:y+1, x:x+radius+1],
                    channel[y:y+radius+1, x-radius:x+1],
                    channel[y:y+radius+1, x:x+radius+1],
                ]
                
                min_var = float('inf')
                chosen_val = 0
                
                for region in regions:
                    var = np.var(region)
                    if var < min_var:
                        min_var = var
                        chosen_val = np.mean(region)
                
                output[y, x, c] = chosen_val
    
    return Image.fromarray(np.clip(output, 0, 255).astype(np.uint8))


def heavy_impasto(img):
    """
    重度厚涂效果 - 强烈笔触质感
    """
    result = img.copy()
    
    # 1. Kuwahara 油画滤镜
    result = kuwahara_heavy(result, radius=4)
    
    # 2. 方向性笔触
    result = apply_brush_strokes(result, stroke_length=12, stroke_strength=0.35)
    
    # 3. 饱和度大幅提升 - 油画色彩
    enhancer = ImageEnhance.Color(result)
    result = enhancer.enhance(1.4)
    
    # 4. 对比度增强
    enhancer = ImageEnhance.Contrast(result)
    result = enhancer.enhance(1.5)
    
    # 5. 锐化笔触边缘
    result = result.filter(ImageFilter.EDGE_ENHANCE_MORE)
    
    # 6. 轻微模糊柔化过渡但保留笔触
    result = result.filter(ImageFilter.GaussianBlur(radius=1))
    result = result.filter(ImageFilter.SHARPEN)
    
    # 7. 亮度调整
    enhancer = ImageEnhance.Brightness(result)
    result = enhancer.enhance(1.1)
    
    return result


def process_sprite_sheet_enhanced(input_path, output_path, grid_size=5):
    """
    处理 5x5 精灵图，应用重度厚涂效果
    """
    print(f"🔄 处理: {input_path}")
    
    img = Image.open(input_path)
    width, height = img.size
    
    cell_width = width // grid_size
    cell_height = height // grid_size
    
    output_img = Image.new('RGBA' if img.mode == 'RGBA' else 'RGB', (width, height))
    
    for row in range(grid_size):
        for col in range(grid_size):
            left = col * cell_width
            top = row * cell_height
            right = left + cell_width
            bottom = top + cell_height
            
            cell = img.crop((left, top, right, bottom))
            
            # 应用重度厚涂
            cell_result = heavy_impasto(cell)
            
            output_img.paste(cell_result, (left, top))
            print(f"   ✓ 单元格 ({row},{col})")
    
    output_img.save(output_path, 'PNG')
    print(f"✅ 已保存: {output_path}")


def main():
    base_dir = "/Users/sika/Desktop/code/CustomSRP/Assets/GameMain/Environment/Textures"
    
    files = [
        ("WaterMist_5x5.png", "WaterMist_5x5_OilHeavy.png"),
        ("WaterfallSplash_5x5.png", "WaterfallSplash_5x5_OilHeavy.png"),
    ]
    
    for input_file, output_file in files:
        input_path = os.path.join(base_dir, input_file)
        output_path = os.path.join(base_dir, output_file)
        
        if os.path.exists(input_path):
            process_sprite_sheet_enhanced(input_path, output_path, grid_size=5)
        else:
            print(f"❌ 文件不存在: {input_path}")


if __name__ == "__main__":
    main()
