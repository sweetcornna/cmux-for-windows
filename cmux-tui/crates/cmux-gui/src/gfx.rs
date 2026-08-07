//! wgpu renderer: instanced background quads under glyphon-shaped text.
//!
//! Cell backgrounds cannot come from glyphon, which only rasterizes glyphs, so
//! they are drawn first as one instanced quad batch and the text pass composites
//! on top within the same render pass.

use std::sync::Arc;

use anyhow::{Context, Result};
use glyphon::{
    Attrs, Buffer, Cache, Color, Family, FontSystem, Metrics, Resolution, Shaping, SwashCache,
    TextArea, TextAtlas, TextBounds, TextRenderer, Viewport,
};
use wgpu::util::DeviceExt as _;
use winit::window::Window;

/// A filled rectangle in physical pixels, optionally rounded.
#[derive(Clone, Copy)]
pub struct Quad {
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
    pub color: [u8; 4],
    pub radius: f32,
}

impl Quad {
    pub fn solid(x: f32, y: f32, w: f32, h: f32, color: [u8; 4]) -> Self {
        Self { x, y, w, h, color, radius: 0.0 }
    }

    pub fn rounded(x: f32, y: f32, w: f32, h: f32, color: [u8; 4], radius: f32) -> Self {
        Self { x, y, w, h, color, radius }
    }
}

/// A shaped text fragment placed at an absolute pixel position.
///
/// Terminal cells and chrome labels share this type; the caller converts grid
/// coordinates to pixels so the renderer stays agnostic about layout.
pub struct TextItem {
    pub text: String,
    pub x: f32,
    pub y: f32,
    pub size: f32,
    pub color: [u8; 4],
    pub bold: bool,
    pub italic: bool,
    /// Clip width, so long session titles truncate instead of overrunning.
    pub max_width: Option<f32>,
}

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct QuadInstance {
    rect: [f32; 4],
    color: [f32; 4],
    radius: f32,
    _pad: [f32; 3],
}

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct Uniforms {
    resolution: [f32; 2],
    _pad: [f32; 2],
}

pub struct Gfx {
    surface: wgpu::Surface<'static>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    config: wgpu::SurfaceConfiguration,

    quad_pipeline: wgpu::RenderPipeline,
    quad_bind_group: wgpu::BindGroup,
    uniform_buffer: wgpu::Buffer,
    instance_buffer: wgpu::Buffer,
    instance_capacity: usize,
    instance_count: u32,

    font_system: FontSystem,
    swash_cache: SwashCache,
    atlas: TextAtlas,
    viewport: Viewport,
    text_renderer: TextRenderer,
    buffers: Vec<(Buffer, f32, f32, Color)>,

    cell_w: f32,
    cell_h: f32,
    metrics: Metrics,
}

impl Gfx {
    pub fn new(window: Arc<Window>, font_size: f32) -> Result<Self> {
        let size = window.inner_size();
        // InstanceDescriptor has no Default in wgpu 30, and `new` takes it by
        // value. PRIMARY keeps this on DX12/Vulkan rather than a GL fallback.
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor {
            backends: wgpu::Backends::PRIMARY,
            flags: wgpu::InstanceFlags::from_env_or_default(),
            memory_budget_thresholds: Default::default(),
            backend_options: Default::default(),
            display: None,
        });
        let surface = instance.create_surface(window).context("create wgpu surface")?;

        let adapter = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::LowPower,
            compatible_surface: Some(&surface),
            force_fallback_adapter: false,
            apply_limit_buckets: false,
        }))
        .context("no suitable GPU adapter")?;

        let (device, queue) =
            pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor::default()))
                .context("request GPU device")?;

        let caps = surface.get_capabilities(&adapter);
        let format = caps
            .formats
            .iter()
            .copied()
            .find(|f| !f.is_srgb())
            .unwrap_or(caps.formats[0]);
        let config = wgpu::SurfaceConfiguration {
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
            format,
            color_space: wgpu::SurfaceColorSpace::Auto,
            width: size.width.max(1),
            height: size.height.max(1),
            present_mode: wgpu::PresentMode::AutoVsync,
            alpha_mode: caps.alpha_modes[0],
            view_formats: vec![],
            desired_maximum_frame_latency: 2,
        };
        surface.configure(&device, &config);

        // Text stack.
        let mut font_system = FontSystem::new();
        let swash_cache = SwashCache::new();
        let cache = Cache::new(&device);
        let viewport = Viewport::new(&device, &cache);
        let mut atlas = TextAtlas::new(&device, &queue, &cache, format);
        let text_renderer =
            TextRenderer::new(&mut atlas, &device, wgpu::MultisampleState::default(), None);

        let metrics = Metrics::new(font_size, (font_size * 1.3).round());
        let (cell_w, cell_h) = measure_cell(&mut font_system, metrics);

        // Background quad pipeline.
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("cmux-gui quad"),
            source: wgpu::ShaderSource::Wgsl(include_str!("quad.wgsl").into()),
        });
        let uniform_buffer = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("cmux-gui uniforms"),
            contents: bytemuck::bytes_of(&Uniforms {
                resolution: [config.width as f32, config.height as f32],
                _pad: [0.0; 2],
            }),
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let bind_group_layout =
            device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
                label: Some("cmux-gui quad bgl"),
                entries: &[wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                }],
            });
        let quad_bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("cmux-gui quad bg"),
            layout: &bind_group_layout,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform_buffer.as_entire_binding(),
            }],
        });
        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("cmux-gui quad layout"),
            bind_group_layouts: &[Some(&bind_group_layout)],
            immediate_size: 0,
        });
        let quad_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("cmux-gui quad pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[Some(wgpu::VertexBufferLayout {
                    array_stride: std::mem::size_of::<QuadInstance>() as u64,
                    step_mode: wgpu::VertexStepMode::Instance,
                    attributes: &wgpu::vertex_attr_array![
                        0 => Float32x4, 1 => Float32x4, 2 => Float32
                    ],
                })],
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format,
                    blend: Some(wgpu::BlendState::ALPHA_BLENDING),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            multiview_mask: None,
            cache: None,
        });

        let instance_capacity = 4096;
        let instance_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("cmux-gui instances"),
            size: (instance_capacity * std::mem::size_of::<QuadInstance>()) as u64,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        Ok(Self {
            surface,
            device,
            queue,
            config,
            quad_pipeline,
            quad_bind_group,
            uniform_buffer,
            instance_buffer,
            instance_capacity,
            instance_count: 0,
            font_system,
            swash_cache,
            atlas,
            viewport,
            text_renderer,
            buffers: Vec::new(),
            cell_w,
            cell_h,
            metrics,
        })
    }

    pub fn cell_size(&self) -> (f32, f32) {
        (self.cell_w, self.cell_h)
    }

    pub fn surface_size(&self) -> (u32, u32) {
        (self.config.width, self.config.height)
    }

    /// Grid dimensions that fit the current surface, clamped to at least 1x1.
    pub fn grid_size(&self) -> (u16, u16) {
        let cols = (self.config.width as f32 / self.cell_w).floor().max(1.0);
        let rows = (self.config.height as f32 / self.cell_h).floor().max(1.0);
        (cols as u16, rows as u16)
    }

    pub fn resize(&mut self, width: u32, height: u32) {
        if width == 0 || height == 0 {
            return;
        }
        self.config.width = width;
        self.config.height = height;
        self.surface.configure(&self.device, &self.config);
        self.queue.write_buffer(
            &self.uniform_buffer,
            0,
            bytemuck::bytes_of(&Uniforms {
                resolution: [width as f32, height as f32],
                _pad: [0.0; 2],
            }),
        );
    }

    /// Re-shape the text layer. Only call when something actually changed.
    pub fn set_text(&mut self, items: &[TextItem]) {
        self.buffers.clear();
        for item in items {
            // Chrome labels and terminal cells use different sizes, so each
            // item carries its own metrics rather than sharing the cell ones.
            let metrics = if (item.size - self.metrics.font_size).abs() < f32::EPSILON {
                self.metrics
            } else {
                Metrics::new(item.size, (item.size * 1.3).round())
            };
            let mut buffer = Buffer::new(&mut self.font_system, metrics);
            buffer.set_size(item.max_width, Some(metrics.line_height));
            let mut attrs = Attrs::new().family(Family::Monospace);
            if item.bold {
                attrs = attrs.weight(glyphon::Weight::BOLD);
            }
            if item.italic {
                attrs = attrs.style(glyphon::Style::Italic);
            }
            buffer.set_text(&item.text, &attrs, Shaping::Advanced, None);
            buffer.shape_until_scroll(&mut self.font_system, false);
            let color = Color::rgba(item.color[0], item.color[1], item.color[2], item.color[3]);
            self.buffers.push((buffer, item.x, item.y, color));
        }
    }

    pub fn set_quads(&mut self, quads: &[Quad]) {
        let instances: Vec<QuadInstance> = quads
            .iter()
            .map(|q| QuadInstance {
                rect: [q.x, q.y, q.w, q.h],
                color: [
                    q.color[0] as f32 / 255.0,
                    q.color[1] as f32 / 255.0,
                    q.color[2] as f32 / 255.0,
                    q.color[3] as f32 / 255.0,
                ],
                radius: q.radius,
                _pad: [0.0; 3],
            })
            .collect();

        if instances.len() > self.instance_capacity {
            self.instance_capacity = instances.len().next_power_of_two();
            self.instance_buffer = self.device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("cmux-gui instances"),
                size: (self.instance_capacity * std::mem::size_of::<QuadInstance>()) as u64,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
        }
        self.instance_count = instances.len() as u32;
        if !instances.is_empty() {
            self.queue.write_buffer(
                &self.instance_buffer,
                0,
                bytemuck::cast_slice(&instances),
            );
        }
    }

    pub fn render(&mut self, clear: [u8; 3]) -> Result<()> {
        use wgpu::CurrentSurfaceTexture as Acquired;
        let frame = match self.surface.get_current_texture() {
            Acquired::Success(frame) => frame,
            // Suboptimal still presents; reconfiguring next frame restores it.
            Acquired::Suboptimal(frame) => frame,
            // Lost and outdated surfaces resolve after a reconfigure.
            Acquired::Lost | Acquired::Outdated => {
                self.surface.configure(&self.device, &self.config);
                return Ok(());
            }
            // Nothing to draw into this frame; try again on the next tick.
            Acquired::Timeout | Acquired::Occluded => return Ok(()),
            Acquired::Validation => {
                return Err(anyhow::anyhow!("surface validation error acquiring frame"));
            }
        };
        let view = frame.texture.create_view(&wgpu::TextureViewDescriptor::default());

        self.viewport.update(
            &self.queue,
            Resolution { width: self.config.width, height: self.config.height },
        );

        let areas: Vec<TextArea<'_>> = self
            .buffers
            .iter()
            .map(|(buffer, x, y, color)| TextArea {
                buffer,
                left: *x,
                top: *y,
                scale: 1.0,
                bounds: TextBounds {
                    left: 0,
                    top: 0,
                    right: self.config.width as i32,
                    bottom: self.config.height as i32,
                },
                default_color: *color,
                custom_glyphs: &[],
            })
            .collect();

        self.text_renderer.prepare(
            &self.device,
            &self.queue,
            &mut self.font_system,
            &mut self.atlas,
            &self.viewport,
            areas,
            &mut self.swash_cache,
        )?;

        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor { label: Some("cmux-gui") });
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("cmux-gui pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color {
                            r: clear[0] as f64 / 255.0,
                            g: clear[1] as f64 / 255.0,
                            b: clear[2] as f64 / 255.0,
                            a: 1.0,
                        }),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });

            if self.instance_count > 0 {
                pass.set_pipeline(&self.quad_pipeline);
                pass.set_bind_group(0, &self.quad_bind_group, &[]);
                pass.set_vertex_buffer(0, self.instance_buffer.slice(..));
                pass.draw(0..6, 0..self.instance_count);
            }

            self.text_renderer.render(&self.atlas, &self.viewport, &mut pass)?;
        }

        self.queue.submit(Some(encoder.finish()));
        // wgpu 30 presents through the queue rather than the texture.
        self.queue.present(frame);
        self.atlas.trim();
        Ok(())
    }
}

/// Advance width and line height of the monospace face, measured rather than
/// assumed so the cell grid lines up with whatever font actually resolves.
fn measure_cell(font_system: &mut FontSystem, metrics: Metrics) -> (f32, f32) {
    const SAMPLE: &str = "MMMMMMMMMM";
    let mut buffer = Buffer::new(font_system, metrics);
    buffer.set_size(None, None);
    let attrs = Attrs::new().family(Family::Monospace);
    buffer.set_text(SAMPLE, &attrs, Shaping::Advanced, None);
    buffer.shape_until_scroll(font_system, false);
    let width = buffer
        .layout_runs()
        .next()
        .map(|run| run.line_w / SAMPLE.chars().count() as f32)
        .filter(|w| *w > 0.0)
        .unwrap_or(metrics.font_size * 0.6);
    (width, metrics.line_height)
}
