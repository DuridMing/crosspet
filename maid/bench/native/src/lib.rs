//! Small CPU compositor shared by the two native benches (MC-39).
//!
//! Pixels are premultiplied BGRA, the format `UpdateLayeredWindowIndirect` takes. This measures drawing and
//! buffer copies only; it opens no window, so the cost of Windows compositing to the screen is not included.
use std::path::Path;
use std::time::{Duration, Instant};

pub const CANVAS_W: usize = 1400;
pub const CANVAS_H: usize = 1500;
/// Sprites are 640 px tall; 200% system scale draws them at this height.
pub const SPRITE_DRAW_H: usize = 1280;
const NOT_POSES: [&str; 3] = ["avatar", "portrait", "portrait-card"];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Rect {
    pub x: usize,
    pub y: usize,
    pub w: usize,
    pub h: usize,
}

impl Rect {
    pub fn union(self, o: Rect) -> Rect {
        let (x, y) = (self.x.min(o.x), self.y.min(o.y));
        let (r, b) = ((self.x + self.w).max(o.x + o.w), (self.y + self.h).max(o.y + o.h));
        Rect { x, y, w: r - x, h: b - y }
    }
    pub fn pixels(self) -> usize {
        self.w * self.h
    }
}

#[derive(Clone)]
pub struct Bitmap {
    pub w: usize,
    pub h: usize,
    pub px: Vec<u8>,
}

impl Bitmap {
    pub fn new(w: usize, h: usize) -> Bitmap {
        Bitmap { w, h, px: vec![0; w * h * 4] }
    }
    pub fn rect(&self) -> Rect {
        Rect { x: 0, y: 0, w: self.w, h: self.h }
    }
}

pub fn decode_webp(path: &Path) -> Bitmap {
    let img = image::open(path).unwrap_or_else(|e| panic!("{}: {e}", path.display())).to_rgba8();
    let (w, h) = (img.width() as usize, img.height() as usize);
    let mut px = img.into_raw();
    for p in px.chunks_exact_mut(4) {
        let a = p[3] as u32;
        let (r, g, b) = (p[0] as u32, p[1] as u32, p[2] as u32);
        p[0] = (b * a / 255) as u8;
        p[1] = (g * a / 255) as u8;
        p[2] = (r * a / 255) as u8;
    }
    Bitmap { w, h, px }
}

/// Decodes every pose sprite in a character folder. Returns the bitmaps and the time spent decoding.
pub fn load_sprites(dir: &Path) -> (Vec<(String, Bitmap)>, Duration) {
    let mut paths: Vec<_> = std::fs::read_dir(dir)
        .unwrap_or_else(|e| panic!("{}: {e}", dir.display()))
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.extension().is_some_and(|x| x == "webp"))
        .filter(|p| !NOT_POSES.contains(&p.file_stem().unwrap_or_default().to_string_lossy().as_ref()))
        .collect();
    paths.sort();
    let t0 = Instant::now();
    let sprites: Vec<_> = paths.iter().map(|p| (p.file_stem().unwrap().to_string_lossy().into_owned(), decode_webp(p))).collect();
    (sprites, t0.elapsed())
}

pub fn scale_bilinear(src: &Bitmap, dw: usize, dh: usize) -> Bitmap {
    let mut dst = Bitmap::new(dw, dh);
    let (sw, sh) = (src.w as u32, src.h as u32);
    for y in 0..dh {
        let fy = (y as u32 * (sh - 1) * 256) / dh.max(1) as u32;
        let (y0, wy) = ((fy >> 8) as usize, fy & 255);
        let y1 = (y0 + 1).min(src.h - 1);
        for x in 0..dw {
            let fx = (x as u32 * (sw - 1) * 256) / dw.max(1) as u32;
            let (x0, wx) = ((fx >> 8) as usize, fx & 255);
            let x1 = (x0 + 1).min(src.w - 1);
            let out = (y * dw + x) * 4;
            for c in 0..4 {
                let p = |xx: usize, yy: usize| src.px[(yy * src.w + xx) * 4 + c] as u32;
                let top = p(x0, y0) * (256 - wx) + p(x1, y0) * wx;
                let bottom = p(x0, y1) * (256 - wx) + p(x1, y1) * wx;
                dst.px[out + c] = ((top * (256 - wy) + bottom * wy) >> 16) as u8;
            }
        }
    }
    dst
}

/// Source-over of `src` placed at (`x`, `y`), touching only pixels inside `clip`.
pub fn blend(dst: &mut Bitmap, src: &Bitmap, x: usize, y: usize, clip: Rect) {
    let left = clip.x.max(x);
    let top = clip.y.max(y);
    let right = (clip.x + clip.w).min(x + src.w).min(dst.w);
    let bottom = (clip.y + clip.h).min(y + src.h).min(dst.h);
    if left >= right || top >= bottom {
        return;
    }
    for row in top..bottom {
        let d0 = (row * dst.w + left) * 4;
        let s0 = ((row - y) * src.w + (left - x)) * 4;
        let n = (right - left) * 4;
        let d = &mut dst.px[d0..d0 + n];
        let s = &src.px[s0..s0 + n];
        for (dp, sp) in d.chunks_exact_mut(4).zip(s.chunks_exact(4)) {
            let inv = 255 - sp[3] as u32;
            if inv == 255 {
                continue;
            }
            for c in 0..4 {
                dp[c] = (sp[c] as u32 + (dp[c] as u32 * inv + 127) / 255) as u8;
            }
        }
    }
}

pub fn clear(dst: &mut Bitmap, r: Rect) {
    for row in r.y..(r.y + r.h).min(dst.h) {
        let start = (row * dst.w + r.x) * 4;
        dst.px[start..start + r.w.min(dst.w - r.x) * 4].fill(0);
    }
}

/// Stands in for the bytes `UpdateLayeredWindowIndirect` copies out for `prcDirty`.
pub fn present(out: &mut Bitmap, src: &Bitmap, r: Rect) {
    for row in r.y..(r.y + r.h).min(src.h) {
        let start = (row * src.w + r.x) * 4;
        let n = r.w.min(src.w - r.x) * 4;
        out.px[start..start + n].copy_from_slice(&src.px[start..start + n]);
    }
}

/// Vertical bob offset in pixels, matching the page's 3.2 s `bob` animation at 60 fps and 200% scale.
pub fn bob_offset(frame: usize) -> usize {
    let t = frame as f64 / 60.0 / 3.2 * std::f64::consts::TAU;
    (12.0 + 12.0 * t.sin()).round() as usize
}

/// A simplified effect tile: a soft round blob, premultiplied.
pub fn effect_tile(size: usize, bgr: [u8; 3]) -> Bitmap {
    let mut b = Bitmap::new(size, size);
    let c = size as f64 / 2.0;
    for y in 0..size {
        for x in 0..size {
            let d = (((x as f64 - c).powi(2) + (y as f64 - c).powi(2)).sqrt() / c).min(1.0);
            let a = (1.0 - d) * 0.8;
            let i = (y * size + x) * 4;
            b.px[i] = (bgr[0] as f64 * a) as u8;
            b.px[i + 1] = (bgr[1] as f64 * a) as u8;
            b.px[i + 2] = (bgr[2] as f64 * a) as u8;
            b.px[i + 3] = (255.0 * a) as u8;
        }
    }
    b
}

pub fn default_sprite_dir() -> std::path::PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../characters/claude")
}

/// Prints one scenario as a JSON line.
pub fn report(scenario: &str, frames: usize, elapsed: Duration, pixels_per_frame: f64) {
    let per_frame = elapsed.as_secs_f64() * 1000.0 / frames as f64;
    println!(
        "{{\"scenario\":\"{scenario}\",\"frames\":{frames},\"ms_per_frame\":{per_frame:.2},\"pct_core_60fps\":{:.1},\"pct_core_30fps\":{:.1},\"mpixels_per_frame\":{:.2}}}",
        per_frame * 60.0 / 10.0,
        per_frame * 30.0 / 10.0,
        pixels_per_frame / 1e6
    );
}
