#!/usr/bin/env python3
"""
优化版厚涂油画效果 - 快速生成高质量笔触效果
使用多尺度模糊 + 边缘检测 + 混合
"""

import numpy as np
from PIL import Image, ImageFilter, ImageEnhance, ImageChops
import os

def oil_painting_effect(img):
    """
    快速厚涂油画效果
    核心：多尺度边缘检测 + 笔触感混合
    """
    # 备份原始
    original = img.copy()
    
    # 1. 多尺度模糊创建油画基底
    blur_sm = img.filter(ImageFilter.GaussianBlur(radius=3))   # 小尺度
    blur_md = img.filter(ImageFilter.GaussianBlur(radius=7))   # 中尺度
    blur_lg = img.filter(ImageFilter.GaussianBlur(radius=15))  # 大尺度
    
    # 2. 边缘检测创建笔触边界
    edges = img.filter(ImageFilter.FIND_EDGES)
    edges = ImageEnhance.Contrast(edges).enhance(2.5)
    
    # 3. 分层混合 - 从大到小尺度混合
    result = Image.blend(blur_lg, blur_md, 0.5)
    result = Image.blend(result, blur_sm, 0.4)
    
    # 4. 添加边缘细节（笔触边界）
    # 使用柔化边缘
    soft_edges = edges.filter(ImageFilter.GaussianBlur(radius=1))
    
    # 5. 增强对比度和饱和度
    result = ImageEnhance.Contrast(result).enhance(1.6)
    result = ImageEnhance.Color(result).enhance(1.4)
    result = ImageEnhance.Brightness(result).enhance(1.05)
    
    # 6. 笔触锐化
    result = result.filter(ImageFilter.SHARPEN)
    result = result.filter(ImageFilter.UnsharpMask(radius=2, percent=150, threshold=3))
    
    # 7. 与原图细节混合
    result = Image.blend(result, original, 0.25)
    
    return result


def brush_stroke_effect(img, direction=45):
    """
    方向性笔触效果
    """
    import math
    
    # 转换为数组
    arr = np.array(img).astype(np.float32)
    
    # 创建方向性卷积核
    size = 5
    kernel = np.zeros((size, size), dtype=np.float32)
    center = size // 2
    
    angle_rad = math.radians(direction)
    cos_a, sin_a = math.cos(angle_rad), math.sin(angle_rad)
    
    for i in range(size):
        x = int(center + (i - center) * cos_a)
        y = int(center + (i - center) * sin_a)
        if 0 <= x < size and 0 <= y < size:
            kernel[y, x] = 1.0
    
    if kernel.sum() > 0:
        kernel = kernel / kernel.sum()
    
    # 应用卷积
    from scipy.ndimage import convolve
    
    if len(arr.shape) == 3:
        channels = []
        for c in range(arr.shape[2]):
            channels.append(convolve(arr[:,:,c], kernel, mode='reflect'))
        arr = np.stack(channels, axis=2)
    else:
        arr = convolve(arr, kernel, mode='reflect')
    
    return Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), mode=img.mode)


def impasto_effect(img):
    """
    厚重涂抹效果 - 模拟真实油画质感
    """
    result = img.copy()
    
    # 多层叠加
    layers = []
    for blur_size in [2, 5, 10]:
        layer = result.filter(ImageFilter.GaussianBlur(radius=blur_size))
        layers.append(layer)
    
    # 混合模糊层
    result = Image.blend(layers[0], layers[1], 0.5)
    result = Image.blend(result, layers[2], 0.3)
    
    # 与原始混合保留细节
    result = Image.blend(result, img, 0.3)
    
    # 对比度增强
    result = ImageEnhance.Contrast(result).enhance(1.5)
    
    # 饱和度增强
    result = ImageEnhance.Color(result).enhance(1.3)
    
    # 锐化边缘
    result = result.filter(ImageFilter.EDGE_ENHANCE_MORE)
    
    # 最后柔化
    result = result.filter(ImageFilter.GaussianBlur(radius=0.5))
    
    return result


def thick_brush_texture(img):
    """
    厚重笔触纹理
    """
    # 创建多个不同方向的笔触层
    result = img.copy()
    
    # 不同角度的方向性模糊
    for angle in [30, 60, 90, 120, 150]:
        try:
            stroked = brush_stroke_effect(img, direction=angle)
            result = Image.blend(result, stroked, 0.2)
        except:
            pass
    
    # 混合原始
    result = Image.blend(result, img, 0.4)
    
    # 增强
    result = ImageEnhance.Contrast(result).enhance(1.4)
    result = ImageEnhance.Color(result).enhance(1.25)
    
    return result


def process_sprite_sheet(input_path, output_path, grid_size=5):
    """
    处理精灵图
    """
    print(f"处理: {input_path}")
    
    img = Image.open(input_path)
    if img.mode != 'RGBA':
        img = img.convert('RGBA')
    
    width, height = img.size
    cell_width = width // grid_size
    cell_height = height // grid_size
    
    output_img = Image.new('RGBA', (width, height))
    
    for row in range(grid_size):
        for col in range(grid_size):
            left = col * cell_width
            top = row * cell_height
            right = left + cell_width
            bottom = top + cell_height
            
            cell = img.crop((left, top, right, bottom))
            
            # 应用效果
            cell = impasto_effect(cell)
            cell = thick_brush_texture(cell)
            
            # 保留 alpha
            original_alpha = cell.split()[3] if cell.mode == 'RGBA' else None
            
            output_img.paste(cell, (left, top))
            print(f"  ({row},{col})", end="", flush=True)
        
        print()
    
    output_img.save(output_path, 'PNG')
    print(f"已保存: {output_path}")


def main():
    base_dir = "/Users/sika/Desktop/code/CustomSRP/Assets/GameMain/Environment/Textures"
    
    files = [
        ("WaterMist_5x5.png", "WaterMist_5x5_OilPainting.png"),
        ("WaterfallSplash_5x5.png", "WaterfallSplash_5x5_OilPainting.png"),
    ]
    
    for input_file, output_file in files:
        input_path = os.path.join(base_dir, input_file)
        output_path = os.path.join(base_dir, output_file)
        
        if os.path.exists(input_path):
            process_sprite_sheet(input_path, output_path, grid_size=5)
        else:
            print(f"文件不存在: {input_path}")


if __name__ == "__main__":
    main()
