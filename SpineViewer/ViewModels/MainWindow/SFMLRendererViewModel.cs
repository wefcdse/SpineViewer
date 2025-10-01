﻿using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandyControl.Expression.Shapes;
using NLog;
using SFMLRenderer;
using SpineViewer.Extensions;
using SpineViewer.Models;
using SpineViewer.Resources;
using SpineViewer.Services;
using SpineViewer.Utils;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SpineViewer.ViewModels.MainWindow
{
    public class SFMLRendererViewModel : ObservableObject
    {
        public ImmutableArray<Stretch> StretchOptions { get; } = Enum.GetValues<Stretch>().ToImmutableArray();

        /// <summary>
        /// 日志器
        /// </summary>
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly MainWindowViewModel _vmMain;
        private readonly ObservableCollectionWithLock<SpineObjectModel> _models;
        private readonly ISFMLRenderer _renderer;
        private readonly ISFMLRenderer _wallpaperRenderer;

        /// <summary>
        /// 被选中对象的背景颜色
        /// </summary>
        private static readonly SFML.Graphics.Color _selectedBackgroundColor = new(255, 255, 255, 50);

        /// <summary>
        /// 被选中对象背景顶点缓冲区
        /// </summary>
        private readonly SFML.Graphics.VertexArray _selectedBackgroundVertices = new(SFML.Graphics.PrimitiveType.Quads, 4); // XXX: 暂时未使用 Dispose 释放

        /// <summary>
        /// 预览画面坐标轴颜色
        /// </summary>
        private static readonly SFML.Graphics.Color _axisColor = new(220, 220, 220);

        /// <summary>
        /// 坐标轴顶点缓冲区
        /// </summary>
        private readonly SFML.Graphics.VertexArray _axisVertices = new(SFML.Graphics.PrimitiveType.Lines, 2); // XXX: 暂时未使用 Dispose 释放

        /// <summary>
        /// 帧间隔计时器
        /// </summary>
        private readonly SFML.System.Clock _clock = new();

        /// <summary>
        /// 渲染任务
        /// </summary>
        private Task? _renderTask = null;
        private CancellationTokenSource? _cancelToken = null;

        /// <summary>
        /// 快进时间量
        /// </summary>
        private float _forwardDelta = 0;
        private readonly object _forwardDeltaLock = new();

        /// <summary>
        /// 背景图片
        /// </summary>
        private SFML.Graphics.Sprite? _backgroundImageSprite; // XXX: 暂时未使用 Dispose 释放
        private SFML.Graphics.Texture? _backgroundImageTexture; // XXX: 暂时未使用 Dispose 释放
        private readonly object _bgLock = new();

        /// <summary>
        /// 临时变量, 记录拖放世界源点
        /// </summary>
        private SFML.System.Vector2f? _draggingSrc = null;

        public SFMLRendererViewModel(MainWindowViewModel vmMain)
        {
            _vmMain = vmMain;
            _models = _vmMain.SpineObjects;
            _renderer = _vmMain.SFMLRenderer;
            _wallpaperRenderer = _vmMain.WallpaperRenderer;
        }

        /// <summary>
        /// 请求选中项发生变化
        /// </summary>
        public event NotifyCollectionChangedEventHandler? RequestSelectionChanging;

        public void SetResolution(uint x, uint y)
        {
            var lastRes = _renderer.Resolution;
            _renderer.Resolution = new(x, y);
            if (lastRes.X != x) OnPropertyChanged(nameof(ResolutionX));
            if (lastRes.Y != y) OnPropertyChanged(nameof(ResolutionY));
        }

        public uint ResolutionX
        {
            get => _renderer.Resolution.X;
            set => SetProperty(_renderer.Resolution.X, value, v => _renderer.Resolution = new(v, _renderer.Resolution.Y));
        }

        public uint ResolutionY
        {
            get => _renderer.Resolution.Y;
            set => SetProperty(_renderer.Resolution.Y, value, v => _renderer.Resolution = new(_renderer.Resolution.X, v));
        }

        public float CenterX
        {
            get => _renderer.Center.X;
            set => SetProperty(_renderer.Center.X, value, v => _renderer.Center = new(v, _renderer.Center.Y));
        }

        public float CenterY
        {
            get => _renderer.Center.Y;
            set => SetProperty(_renderer.Center.Y, value, v => _renderer.Center = new(_renderer.Center.X, v));
        }

        public float Zoom
        {
            get => _renderer.Zoom;
            set => SetProperty(_renderer.Zoom, value, v => _renderer.Zoom = value);
        }

        public float Rotation
        {
            get => _renderer.Rotation;
            set => SetProperty(_renderer.Rotation, value, v => _renderer.Rotation = value);
        }

        public bool FlipX
        {
            get => _renderer.FlipX;
            set => SetProperty(_renderer.FlipX, value, v => _renderer.FlipX = value);
        }

        public bool FlipY
        {
            get => _renderer.FlipY;
            set => SetProperty(_renderer.FlipY, value, v => _renderer.FlipY = value);
        }

        public uint MaxFps
        {
            get => _renderer.MaxFps;
            set => SetProperty(_renderer.MaxFps, value, v => _renderer.MaxFps = value);
        }

        public float Speed
        {
            get => _speed;
            set => SetProperty(ref _speed, Math.Clamp(value, 0.01f, 100f));
        }
        private float _speed = 1f;

        public bool ShowAxis
        {
            get => _showAxis;
            set => SetProperty(ref _showAxis, value);
        }
        private bool _showAxis = true;

        public Color BackgroundColor
        {
            get => Color.FromRgb(_backgroundColor.R, _backgroundColor.G, _backgroundColor.B);
            set => SetProperty(BackgroundColor, value, v => _backgroundColor = new(value.R, value.G, value.B));
        }
        private SFML.Graphics.Color _backgroundColor = new(105, 105, 105);

        public string BackgroundImagePath
        {
            get => _backgroundImagePath;
            set => SetProperty(_backgroundImagePath, value, v =>
            {
                if (string.IsNullOrWhiteSpace(v))
                {
                    lock (_bgLock)
                    {
                        _backgroundImageSprite?.Dispose();
                        _backgroundImageTexture?.Dispose();
                        _backgroundImageTexture = null;
                        _backgroundImageSprite = null;
                    }
                    _backgroundImagePath = v;
                }
                else
                {
                    if (!File.Exists(v))
                    {
                        _logger.Warn("Omit non-existed background image path, {0}", v);
                        return;
                    }
                    SFML.Graphics.Texture tex = null;
                    SFML.Graphics.Sprite sprite = null;
                    try
                    {
                        tex = new(v);
                        sprite = new(tex) { Origin = new(tex.Size.X / 2f, tex.Size.Y / 2f) };
                        lock (_bgLock)
                        {
                            _backgroundImageSprite?.Dispose();
                            _backgroundImageTexture?.Dispose();
                            _backgroundImageTexture = tex;
                            _backgroundImageSprite = sprite;
                        }
                        _backgroundImagePath = v;
                        _logger.Info("Load background image from {0}", v);
                        _logger.LogCurrentProcessMemoryUsage();
                    }
                    catch (Exception ex)
                    {
                        sprite?.Dispose();
                        tex?.Dispose();
                        _logger.Error("Failed to load background image from path: {0}, {1}", v, ex.Message);
                    }
                }
            });
        }
        private string _backgroundImagePath;

        public Stretch BackgroundImageMode
        {
            get => _backgroundImageMode;
            set => SetProperty(ref _backgroundImageMode, value);
        }
        private Stretch _backgroundImageMode = Stretch.Uniform;

        public bool WallpaperView
        {
            get => _wallpaperView;
            set => SetProperty(ref _wallpaperView, value);
        }
        private bool _wallpaperView;

        public bool RenderSelectedOnly
        {
            get => _renderSelectedOnly;
            set => SetProperty(ref _renderSelectedOnly, value);
        }
        private bool _renderSelectedOnly = false;

        public bool IsUpdating
        {
            get => _isUpdating;
            private set
            {
                if (value == _isUpdating) return;
                _isUpdating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Geo_PlayPause));
            }
        }
        private bool _isUpdating = true;

        public RelayCommand Cmd_SelectBackgroundImage => _cmd_SelectBackgroundImage ??= new(() =>
        {
            if (!DialogService.ShowOpenSFMLImageDialog(out var fileName))
                return;
            BackgroundImagePath = fileName;
        });
        private RelayCommand? _cmd_SelectBackgroundImage;

        public RelayCommand Cmd_Stop => _cmd_Stop ??= new(() =>
        {
            IsUpdating = false;
            lock (_models) foreach (var sp in _models) sp.ResetAnimationsTime();
        });
        private RelayCommand? _cmd_Stop;

        public RelayCommand Cmd_PlayPause => _cmd_PlayPause ??= new(() => IsUpdating = !IsUpdating);
        private RelayCommand? _cmd_PlayPause;

        public Geometry Geo_PlayPause => _isUpdating ? AppResource.Geo_Pause : AppResource.Geo_Play;

        public RelayCommand Cmd_Restart => _cmd_Restart ??= new(() =>
        {
            lock (_models) foreach (var sp in _models) sp.ResetAnimationsTime();
            IsUpdating = true;
        });
        private RelayCommand? _cmd_Restart;

        public RelayCommand Cmd_ForwardStep => _cmd_ForwardStep ??= new(() =>
        {
            lock (_forwardDeltaLock) _forwardDelta += _renderer.MaxFps > 0 ? 1f / _renderer.MaxFps : 0.001f;
        });
        private RelayCommand? _cmd_ForwardStep;

        public RelayCommand Cmd_ForwardFast => _cmd_ForwardFast ??= new(() =>
        {
            lock (_forwardDeltaLock) _forwardDelta += _renderer.MaxFps > 0 ? 10f / _renderer.MaxFps : 0.01f;
        });
        private RelayCommand? _cmd_ForwardFast;

        public void CanvasMouseWheelScrolled(object? s, SFML.Window.MouseWheelScrollEventArgs e)
        {
            var factor = e.Delta > 0 ? 1.1f : 0.9f;
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                Zoom = Math.Clamp(Zoom * factor, 0.001f, 1000f); // 滚轮缩放限制一下缩放范围
            }
            else
            {
                lock (_models.Lock)
                {
                    foreach (var sp in _models.Where(it => it.IsShown && it.IsSelected))
                    {
                        sp.Scale = Math.Clamp(sp.Scale * factor, 0.001f, 1000f); // 滚轮缩放限制一下缩放范围
                    }
                }
            }
        }
        private class GetSize :  SFML.Graphics.Drawable
        {
            public SFML.System.Vector2u Size;
            public GetSize()
            {
                this.Size = new SFML.System.Vector2u(0, 0);
            }

        

            public void Draw(SFML.Graphics.RenderTarget target, SFML.Graphics.RenderStates states)
            {
                this.Size = target.Size;
            }
        }

        public void CanvasMouseButtonPressed(object? s, SFML.Window.MouseButtonEventArgs e)
        {
            if (e.Button == SFML.Window.Mouse.Button.Right)
            {
                _draggingSrc = _renderer.MapPixelToCoords(new(e.X, e.Y));
            }
            else if (e.Button == SFML.Window.Mouse.Button.Left && !SFML.Window.Mouse.IsButtonPressed(SFML.Window.Mouse.Button.Right))
            {
                var _src = _renderer.MapPixelToCoords(new(e.X, e.Y));
                var src = new Point(_src.X, _src.Y);
                _draggingSrc = _src;

                lock (_models.Lock)
                {
                    // 仅渲染选中模式禁止在画面里选择对象
                    if (_renderSelectedOnly)
                    {
                        // 只在被选中的对象里判断是否有效命中
                        bool hit = _models.Any(m => m.IsSelected && m.GetCurrentBounds().Contains(src));

                        // 如果没点到被选中的模型, 则不允许拖动
                        if (!hit) _draggingSrc = null;
                    }
                    else
                    {
                        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                        {
                            // 没按 Ctrl 的情况下, 如果命中了已选中对象, 则就算普通命中
                            bool hitany = false;
                            foreach (var sp in _models)
                            {
                                if (!sp.IsShown) continue;
                                if (!sp.GetCurrentBounds().Contains(src)) continue;


                                var getSize = new GetSize();
                                this._renderer.Draw(getSize);
                                Debug.Print("window size: " + getSize.Size);
                                var hit = sp.TestHit(e.X, e.Y, getSize.Size, _renderer.GetView());
                                hitany = hit || hitany;
                                // 如果点到了没被选中的东西, 则清空原先选中的, 改为只选中这一次点的
                                if (!sp.IsSelected && hit)
                                {
                                    RequestSelectionChanging?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
                                    RequestSelectionChanging?.Invoke(this, new(NotifyCollectionChangedAction.Add, sp));
                                }
                                if (hit)
                                {
                                    var slots = sp.TesctSlotHit(e.X, e.Y, getSize.Size, _renderer.GetView());
                                    if (slots.Length > 0)
                                    {
                                        var info = "hit slot: [";
                                        var sep = "";
                                        foreach (var slot in slots)
                                        {
                                            info += sep;
                                            info += slot.Name;
                                            sep = ", ";
                                        }
                                        info += "]";
                                        Views.MainWindow.Logger.Info(info);
                                    }

                                    break;
                                }
                            }

                            // 如果点了空白的地方, 就清空选中列表
                            if (!hitany) RequestSelectionChanging?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
                        }
                        else
                        {
                            // 按下 Ctrl 的情况就执行多选, 并且点空白处也不会清空选中, 如果点击了本来就是选中的则取消选中
                            if (_models.FirstOrDefault(m => m.IsShown && m.GetCurrentBounds().Contains(src), null) is SpineObjectModel sp)
                            {
                                if (sp.IsSelected)
                                    RequestSelectionChanging?.Invoke(this, new(NotifyCollectionChangedAction.Remove, sp));
                                else
                                    RequestSelectionChanging?.Invoke(this, new(NotifyCollectionChangedAction.Add, sp));
                            }
                        }
                    }
                }
            }
        }

        public void CanvasMouseMove(object? s, SFML.Window.MouseMoveEventArgs e)
        {
            if (_draggingSrc is null) return;

            var src = (SFML.System.Vector2f)_draggingSrc;
            var dst = _renderer.MapPixelToCoords(new(e.X, e.Y));
            var delta = dst - src;

            if (SFML.Window.Mouse.IsButtonPressed(SFML.Window.Mouse.Button.Right))
            {
                _renderer.Center -= delta;
                OnPropertyChanged(nameof(CenterX));
                OnPropertyChanged(nameof(CenterY));
            }
            else if (SFML.Window.Mouse.IsButtonPressed(SFML.Window.Mouse.Button.Left))
            {
                lock (_models.Lock)
                {
                    foreach (var sp in _models.Where(it => it.IsShown && it.IsSelected))
                    {
                        sp.X += delta.X;
                        sp.Y += delta.Y;
                    }
                }
                _draggingSrc = dst;
            }
        }

        public void CanvasMouseButtonReleased(object? s, SFML.Window.MouseButtonEventArgs e)
        {
            if (e.Button == SFML.Window.Mouse.Button.Right)
            {
                _draggingSrc = null;
            }
            else if (e.Button == SFML.Window.Mouse.Button.Left && !SFML.Window.Mouse.IsButtonPressed(SFML.Window.Mouse.Button.Right))
            {
                _draggingSrc = null;
            }
        }

        public void StartRender()
        {
            if (_renderTask is not null) return;
            _cancelToken = new();
            _renderTask = new Task(RenderTask, _cancelToken.Token, TaskCreationOptions.LongRunning);
            _renderTask.Start();
            IsUpdating = true;
        }

        public void StopRender()
        {
            IsUpdating = false;
            if (_renderTask is null || _cancelToken is null) return;
            _cancelToken.Cancel();
            _renderTask.Wait();
            _cancelToken = null;
            _renderTask = null;
        }

        private void RenderTask()
        {
            try
            {
                _wallpaperRenderer.SetActive(true);
                _renderer.SetActive(true);

                float delta;
                while (!_cancelToken?.IsCancellationRequested ?? false)
                {
                    delta = _clock.ElapsedTime.AsSeconds();
                    _clock.Restart();

                    // 停止更新的时候只是时间不前进, 但是坐标变换还是要更新, 否则无法移动对象
                    if (!_isUpdating) delta = 0;

                    // 加上要快进的量
                    lock (_forwardDeltaLock)
                    {
                        delta += _forwardDelta;
                        _forwardDelta = 0;
                    }

                    using var v = _renderer.GetView();
                    _renderer.Clear(_backgroundColor);

                    if (_wallpaperView)
                    {
                        _wallpaperRenderer.SetView(v);
                        _wallpaperRenderer.Clear(_backgroundColor);
                    }

                    // 渲染背景
                    lock (_bgLock)
                    {
                        if (_backgroundImageSprite is not null)
                        {
                            using var view = _renderer.GetView();
                            var bg = _backgroundImageSprite;
                            var viewSize = view.Size;
                            var bgSize = bg.Texture.Size;
                            var scaleX = Math.Abs(viewSize.X / bgSize.X);
                            var scaleY = Math.Abs(viewSize.Y / bgSize.Y);
                            var signX = Math.Sign(viewSize.X);
                            var signY = Math.Sign(viewSize.Y);
                            if (_backgroundImageMode == Stretch.None)
                            {
                                scaleX = scaleY = 1f / _renderer.Zoom;
                            }
                            else if (_backgroundImageMode == Stretch.Uniform)
                            {
                                scaleX = scaleY = Math.Min(scaleX, scaleY);
                            }
                            else if (_backgroundImageMode == Stretch.UniformToFill)
                            {
                                scaleX = scaleY = Math.Max(scaleX, scaleY);
                            }
                            bg.Scale = new(signX * scaleX, signY * scaleY);
                            bg.Position = view.Center;
                            bg.Rotation = view.Rotation;
                            _renderer.Draw(bg);

                            if (_wallpaperView)
                            {
                                _wallpaperRenderer.Draw(bg);
                            }
                        }
                    }

                    if (_showAxis)
                    {
                        // 画一个很长的坐标轴, 用 1e9 比较合适
                        _axisVertices[0] = new(new(-1e9f, 0), _axisColor);
                        _axisVertices[1] = new(new(1e9f, 0), _axisColor);
                        _renderer.Draw(_axisVertices);
                        _axisVertices[0] = new(new(0, -1e9f), _axisColor);
                        _axisVertices[1] = new(new(0, 1e9f), _axisColor);
                        _renderer.Draw(_axisVertices);
                    }

                    // 渲染 Spine
                    lock (_models.Lock)
                    {
                        foreach (var sp in _models.Where(sp => sp.IsShown && (!_renderSelectedOnly || sp.IsSelected)).Reverse())
                        {
                            if (_cancelToken?.IsCancellationRequested ?? true) break; // 提前中止

                            sp.Update(0); // 避免物理效果出现问题
                            sp.Update(delta * _speed);

                            // 为选中对象绘制一个半透明背景
                            if (sp.IsSelected)
                            {
                                var rc = sp.GetCurrentBounds().ToFloatRect();
                                _selectedBackgroundVertices[0] = new(new(rc.Left, rc.Top), _selectedBackgroundColor);
                                _selectedBackgroundVertices[1] = new(new(rc.Left + rc.Width, rc.Top), _selectedBackgroundColor);
                                _selectedBackgroundVertices[2] = new(new(rc.Left + rc.Width, rc.Top + rc.Height), _selectedBackgroundColor);
                                _selectedBackgroundVertices[3] = new(new(rc.Left, rc.Top + rc.Height), _selectedBackgroundColor);
                                _renderer.Draw(_selectedBackgroundVertices);
                            }

                            // 仅在预览画面临时启用调试模式
                            sp.EnableDebug = true;
                            _renderer.Draw(sp);
                            sp.EnableDebug = false;

                            if (_wallpaperView)
                            {
                                _wallpaperRenderer.Draw(sp);
                            }
                        }
                    }

                    _renderer.Display();

                    if (_wallpaperView)
                    {
                        _wallpaperRenderer.Display();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Trace(ex.ToString());
                _logger.Fatal("Render task stopped, {0}", ex.Message);
                MessagePopupService.Error(ex.ToString());
            }
            finally
            {
                _renderer.SetActive(false);
                _wallpaperRenderer.SetActive(false);
            }
        }

        public RendererWorkspaceConfigModel WorkspaceConfig
        {
            get
            {
                return new()
                {
                    ResolutionX = ResolutionX,
                    ResolutionY = ResolutionY,
                    CenterX = CenterX,
                    CenterY = CenterY,
                    Zoom = Zoom,
                    Rotation = Rotation,
                    FlipX = FlipX,
                    FlipY = FlipY,
                    MaxFps = MaxFps,
                    Speed = Speed,
                    ShowAxis = ShowAxis,
                    BackgroundColor = BackgroundColor,
                    BackgroundImagePath = BackgroundImagePath,
                    BackgroundImageMode = BackgroundImageMode,
                };
            }
            set
            {
                SetResolution(value.ResolutionX, value.ResolutionY);
                CenterX = value.CenterX;
                CenterY = value.CenterY;
                Zoom = value.Zoom;
                Rotation = value.Rotation;
                FlipX = value.FlipX;
                FlipY = value.FlipY;
                MaxFps = value.MaxFps;
                Speed = value.Speed;
                ShowAxis = value.ShowAxis;
                BackgroundColor = value.BackgroundColor;
                BackgroundImagePath = value.BackgroundImagePath;
                BackgroundImageMode = value.BackgroundImageMode;
            }
        }
    }
}
