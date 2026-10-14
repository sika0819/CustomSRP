#!/usr/bin/env python3
"""
增强版厚涂油画效果生成器
使用卷积核模拟真实笔触 + Kuwahara 滤镜
"""

import numpy as np
from PIL import Image, ImageFilter, ImageEnhance, ImageDraw
import os
from scipy import ndimage

def create_brush_stroke_kernel(angle=45, length=5, width=3):
    """
    创建笔触卷积核 - 模拟真实画笔
    angle: 笔触角度
    length: 笔触长度
    width: 笔触宽度
    """
    size = max(length, width) * 2 + 1
    kernel = np.zeros((size, size))
    center = size // 2
    
    # 沿角度方向绘制笔触
    for i in range(-length//2, length//2):
        for j in range(-width//2, width//2):
            x = int(center + i * np.cos(np.radians(angle)) - j * np.sin(np.radians(angle)))
            y = int(center + i * np.sin(np.radians(angle)) + j * np.cos(np.radians(angle)))
            if 0 <= x < size and 0 <= y < size:
                kernel[y, x] = 1
    
    kernel = kernel / kernel.sum() if kernel.sum() > 0 else kernel
    return kernel.astype(np.float32)


def directional_blur(img, angle, strength=3):
    """
    方向性模糊 - 模拟笔触方向
    """
    # 创建沿指定角度的线核
    size = strength * 2 + 1
    kernel = np.zeros((size, size))
    center = size // 2
    
    for i in range(size):
        x = int(center + (i - center) * np.cos(np.radians(angle)))
        y = int(center + (i - center) * np.sin(np.radians(angle)))
        if 0 <= x < size and 0 <= y < size:
            kernel[center, i] = 1
    
    kernel = kernel / kernel.sum()
    
    # 应用卷积
    from scipy.ndimage import convolve
    if img.mode == 'RGBA':
        r, g, b, a = img.split()
        r = np.array(r).astype(np.float64)
        g = np.array(g).astype(np.float64)
        b = np.array(b).astype(np.float64)
        
        r = convolve(r, kernel, mode='reflect')
        g = convolve(g, kernel, mode='reflect')
        b = convolve(b, kernel, mode='reflect')
        
        r = np.clip(r, 0, 255).astype(np.uint8)
        g = np.clip(g, 0, 255).astype(np.uint8)
        b = np.clip(b, 0, 255).astype(np.uint8)
        
        return Image.merge('RGBA', (Image.fromarray(r), Image.fromarray(g), Image.fromarray(b), a))
    else:
        arr = np.array(img).astype(np.float64)
        arr = convolve(arr, kernel, mode='reflect')
        arr = np.clip(arr, 0, 255).astype(np.uint8)
        return Image.fromarray(arr)


def kuwahara_filter_fast(img_array, radius=3):
    """
    快速 Kuwahara 滤镜 - 经典油画算法
    """
    height, width = img_array.shape[:2]
    output = np.zeros_like(img_array)
    
    for c in range(img_array.shape[2] if len(img_array.shape) == 3 else 1):
        if len(img_array.shape) == 3:
            channel = img_array[:, :, c]
        else:
            channel = img_array
        
        result = np.zeros_like(channel, dtype=np.float64)
        
        # 简化为 2x2 区域以提高速度
        for y in range(radius, height - radius):
            for x in range(radius, width - radius):
                # 4个象限
                q1 = channel[y-radius:y, x:x+radius]    # 左上
                q2 = channel[y-radius:y, x+1:x+radius+1]  # 右上
                q3 = channel[y+1:y+radius+1, x:x+radius]  # 左下
                q4 = channel[y+1:y+radius+1, x+1:x+radius+1]  # 右下
                
                regions = [q1, q2, q3, q4]
                
                min_var = float('inf')
                chosen_mean = channel[y, x]
                
                for region in regions:
                    mean = np.mean(region)
                    var = np.var(region)
                    if var < min_var:
                        min_var = var
                        chosen_mean = mean
                
                result[y, x] = chosen_mean
        
        if len(img_array.shape) == 3:
            output[:, :, c] = result
        else:
            output = result
    
    return np.clip(output, 0, 255).astype(np.uint8)


def heavy_impasto_oil_painting(img):
    """
    厚重涂抹油画效果 - 多层处理
    """
    result = img.copy()
    
    # 转换为 numpy 数组
    arr = np.array(result)
    
    # 应用 Kuwahara 滤镜（简化版）
    arr = kuwahara_filter_fast(arr, radius=2)
    result = Image.fromarray(arr)
    
    # 多层方向性模糊 - 模拟笔触
    angles = [30, 60, 120, 150]
    for angle in angles:
        result = directional_blur(result, angle, strength=2)
    
    # 混合原始细节
    arr_original = np.array(img)
    arr_processed = np.array(result)
    arr_mixed = (arr_original * 0.3 + arr_processed * 0.7).astype(np.uint8)
    result = Image.fromarray(arr_mixed)
    
    # 对比度增强
    enhancer = ImageEnhance.Contrast(result)
    result = enhancer.enhance(1.5)
    
    # 饱和度提升
    enhancer = ImageEnhance.Color(result)
    result = enhancer.enhance(1.3)
    
    # 亮度微调
    enhancer = ImageEnhance.Brightness(result)
    result = enhancer.enhance(1.05)
    
    # 纹理锐化
    result = result.filter(ImageFilter.UnsharpMask(radius=1.5, percent=120, threshold=2))
    
    # 边缘增强 - 笔触感
    result = result.filter(ImageFilter.EDGE_ENHANCE)
    
    # 最后轻微模糊融合
    result = result.filter(ImageFilter.GaussianBlur(radius=0.5))
    
    return result


def add_canvas_texture(img, strength=0.15):
    """
    添加画布纹理
    """
    # 创建带纹理的副本
    arr = np.array(img).astype(np.float32)
    
    # 添加随机噪声模拟画布纹理
    noise = (np.random.rand(arr.shape[0], arr.shape[1], arr.shape[2] if len(arr.shape) == 3 else 1) - 0.5) * 40
    
    if len(arr.shape) == 3:
        arr = arr + noise[:, :, :arr.shape[2]]
    else:
        arr = arr + noise.squeeze()
    
    arr = np.clip(arr, 0, 255).astype(np.uint8)
    textured = Image.fromarray(arr, mode=img.mode)
    
    # 与原图混合
    return Image.blend(img, textured, strength)


def process_sprite_sheet_v2(input_path, output_path, grid_size=5, prompt=""):
    """
    处理精灵图 - 应用厚涂油画效果
    """
    print(f"处理: {input_path}")
    if prompt:
        print(f"提示词: {prompt}")
    
    # 打开原图
    img = Image.open(input_path)
    
    # 确保 RGBA 模式
    if img.mode != 'RGBA':
        img = img.convert('RGBA')
    
    width, height = img.size
    
    # 计算每个子图的大小
    cell_width = width // grid_size
    cell_height = height // grid_size
    
    # 创建输出画布
    output_img = Image.new('RGBA', (width, height))
    
    # 逐个处理每个子图
    for row in range(grid_size):
        for col in range(grid_size):
            # 裁剪子图
            left = col * cell_width
            top = row * cell_height
            right = left + cell_width
            bottom = top + cell_height
            
            cell = img.crop((left, top, right, bottom))
            
            # 应用厚涂油画效果
            cell_oil = heavy_impasto_oil_painting(cell)
            
            # 添加画布纹理
            cell_oil = add_canvas_texture(cell_oil, strength=0.1)
            
            # 确保有 alpha 通道
            if cell_oil.mode != 'RGBA':
                cell_oil = cell_oil.convert('RGBA')
            
            # 保留原始 alpha 通道
            original_alpha = cell.split()[3] if cell.mode == 'RGBA' else None
            if original_alpha:
                r, g, b, _ = cell_oil.split()
                cell_oil = Image.merge('RGBA', (r, g, b, original_alpha))
            
            # 粘贴回画布
            output_img.paste(cell_oil, (left, top))
            
            print(f"  处理单元格 ({row},{col})...")
    
    # 保存
    output_img.save(output_path, 'PNG')
    print(f"已保存: {output_path}")


def main():
    base_dir = "/Users/sika/Desktop/code/CustomSRP/Assets/GameMain/Environment/Textures"
    
    files = [
        (
            "WaterMist_5x5.png", 
            "WaterMist_5x5_OilPainting.png",
            "Thick impasto oil painting of water mist particles, visible brush strokes, thick paint texture, soft blue and white tones"
        ),
        (
            "WaterfallSplash_5x5.png", 
            "WaterfallSplash_5x5_OilPainting.png",
            "Thick impasto oil painting of waterfall splash, visible brush strokes, thick paint texture, dynamic water spray"
        ),
    ]
    
    for input_file, output_file, prompt in files:
        input_path = os.path.join(base_dir, input_file)
        output_path = os.path.join(base_dir, output_file)
        
        if os.path.exists(input_path):
            process_sprite_sheet_v2(input_path, output_path, grid_size=5, prompt=prompt)
        else:
            print(f"文件不存在: {input_path}")


if __name__ == "__main__":
    main()
