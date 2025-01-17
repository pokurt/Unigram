//
// Copyright Fela Ameghino 2015-2025
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using System;
using System.Numerics;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Gallery;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls.Gallery
{
    public sealed partial class GalleryContent : AspectView
    {
        private IGalleryDelegate _delegate;
        private GalleryMedia _item;

        private int _itemId;

        public GalleryMedia Item => _item;

        private long _fileToken;
        private long _thumbnailToken;

        private int _appliedId;

        private Stretch _appliedStretch;
        private int _appliedRotation;

        private bool _fromSizeChanged;

        public bool IsEnabled
        {
            get => Button.IsEnabled;
            set => Button.IsEnabled = value;
        }

        public GalleryContent()
        {
            InitializeComponent();

            RotationAngleChanged += OnRotationAngleChanged;
            SizeChanged += OnSizeChanged;

            Texture.ImageOpened += OnImageOpened;
        }

        private void OnImageOpened(object sender, RoutedEventArgs e)
        {
            MediaOpened();
        }

        private void MediaOpened()
        {
            if (_item is GalleryMessage message && message.HasProtectedContent)
            {
                UpdateManager.Unsubscribe(this, ref _fileToken);

                _delegate.ClientService?.Send(new OpenMessageContent(message.ChatId, message.Id));
            }
        }

        private void OnRotationAngleChanged(object sender, RoutedEventArgs e)
        {
            if (_fromSizeChanged)
            {
                return;
            }

            OnSizeChanged(sender, null);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_item == null || _itemId != _appliedId)
            {
                _appliedId = _itemId;
                return;
            }

            _appliedId = _itemId;

            var angle = RotationAngle switch
            {
                RotationAngle.Angle90 => 90,
                RotationAngle.Angle180 => 180,
                RotationAngle.Angle270 => 270,
                _ => 0
            };

            var visual = ElementComposition.GetElementVisual(this);
            visual.CenterPoint = new Vector3(ActualSize / 2, 0);
            visual.Clip ??= visual.Compositor.CreateInsetClip();

            if (_appliedStretch == Stretch && _appliedRotation == angle)
            {
                visual.RotationAngleInDegrees = angle;
                return;
            }

            _appliedStretch = Stretch;
            _fromSizeChanged = e != null;

            if (e != null)
            {
                var prev = e.PreviousSize.ToVector2();
                var next = e.NewSize.ToVector2();

                var anim = BootStrapper.Current.Compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(0, new Vector3(prev / next, 1));
                anim.InsertKeyFrame(1, Vector3.One);

                var panel = ElementComposition.GetElementVisual(Children[0]);
                panel.CenterPoint = new Vector3(next.X / 2, next.Y / 2, 0);
                panel.StartAnimation("Scale", anim);

                var factor = BootStrapper.Current.Compositor.CreateExpressionAnimation("Vector3(1 / content.Scale.X, 1 / content.Scale.Y, 1)");
                factor.SetReferenceParameter("content", panel);

                var button = ElementComposition.GetElementVisual(Button);
                button.CenterPoint = new Vector3(Button.ActualSize.X / 2, Button.ActualSize.Y / 2, 0);
                button.StartAnimation("Scale", factor);
            }

            if (_appliedRotation != angle)
            {
                var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
                animation.InsertKeyFrame(0, angle > _appliedRotation ? 360 : _appliedRotation);
                animation.InsertKeyFrame(1, angle);

                _appliedRotation = angle;
                visual.StartAnimation("RotationAngleInDegrees", animation);
            }
        }

        public void UpdateItem(IGalleryDelegate delegato, GalleryMedia item)
        {
            _delegate = delegato;
            _item = item;

            _appliedRotation = item?.RotationAngle switch
            {
                RotationAngle.Angle90 => 90,
                RotationAngle.Angle180 => 180,
                RotationAngle.Angle270 => 270,
                _ => 0
            };

            Tag = item;
            RotationAngle = item?.RotationAngle ?? RotationAngle.Angle0;
            Background = null;
            Texture.Source = null;

            //ScrollingHost.ChangeView(0, 0, 1, true);

            var file = item?.File;
            if (file == null)
            {
                return;
            }

            _itemId = file.Id;

            if (item.IsVideoNote)
            {
                MaxWidth = 384;
                MaxHeight = 384;

                CornerRadius = new CornerRadius(384 / 2);
                Constraint = new Size(384, 384);
            }
            else
            {
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;

                CornerRadius = new CornerRadius(0);
                Constraint = item.Constraint;
            }

            var thumbnail = item.Thumbnail;
            if (thumbnail != null && (item.IsVideo || (item.IsPhoto && !file.Local.IsDownloadingCompleted)))
            {
                UpdateThumbnail(item, thumbnail, null, true);
            }

            UpdateManager.Subscribe(this, delegato.ClientService, file, ref _fileToken, UpdateFile);
            UpdateFile(item, file);
        }

        private void UpdateFile(object target, File file)
        {
            UpdateFile(_item, file);
        }

        private void UpdateFile(GalleryMedia item, File file)
        {
            var reference = item?.File;
            if (reference == null || reference.Id != file.Id)
            {
                return;
            }

            var size = Math.Max(file.Size, file.ExpectedSize);
            if (file.Local.IsDownloadingActive)
            {
                Button.SetGlyph(file.Id, MessageContentState.Downloading);
                Button.Progress = (double)file.Local.DownloadedSize / size;
                Button.Opacity = 1;
            }
            else if (file.Remote.IsUploadingActive)
            {
                Button.SetGlyph(file.Id, MessageContentState.Uploading);
                Button.Progress = (double)file.Remote.UploadedSize / size;
                Button.Opacity = 1;
            }
            else if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingCompleted)
            {
                Button.SetGlyph(file.Id, MessageContentState.Download);
                Button.Progress = 0;
                Button.Opacity = 1;

                if (item.IsPhoto)
                {
                    item.ClientService.DownloadFile(file.Id, 1);
                }
            }
            else
            {
                if (item.IsVideo)
                {
                    Button.SetGlyph(file.Id, MessageContentState.Play);
                    Button.Progress = 1;
                    Button.Opacity = 1;
                }
                else if (item.IsPhoto)
                {
                    Button.SetGlyph(file.Id, MessageContentState.Photo);
                    Button.Opacity = 0;

                    Texture.Source = UriEx.ToBitmap(file.Local.Path, 0, 0);
                }
            }

            Canvas.SetZIndex(Button,
                Button.State == MessageContentState.Photo ? -1 : 0);
        }

        private void UpdateThumbnail(object target, File file)
        {
            UpdateThumbnail(_item, file, null, false);
        }

        private void UpdateThumbnail(GalleryMedia item, File file, Minithumbnail minithumbnail, bool download)
        {
            BitmapImage source = null;
            ImageBrush brush;

            if (Background is ImageBrush existing)
            {
                brush = existing;
            }
            else
            {
                brush = new ImageBrush
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };

                Background = brush;
            }

            if (file != null)
            {
                if (file.Local.IsDownloadingCompleted)
                {
                    source = new BitmapImage();
                    PlaceholderHelper.GetBlurred(source, file.Local.Path, 3);
                }
                else
                {
                    if (download)
                    {
                        if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
                        {
                            _delegate.ClientService.DownloadFile(file.Id, 1);
                        }

                        UpdateManager.Subscribe(this, _delegate.ClientService, file, ref _thumbnailToken, UpdateThumbnail, true);
                    }

                    if (minithumbnail != null)
                    {
                        source = new BitmapImage();
                        PlaceholderHelper.GetBlurred(source, minithumbnail.Data, 3);
                    }
                }
            }
            else if (minithumbnail != null)
            {
                source = new BitmapImage();
                PlaceholderHelper.GetBlurred(source, minithumbnail.Data, 3);
            }

            brush.ImageSource = source;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var item = _item;
            if (item == null)
            {
                return;
            }

            var file = item.File;
            if (file == null)
            {
                return;
            }

            if (file.Local.IsDownloadingActive)
            {
                item.ClientService.Send(new CancelDownloadFile(file.Id, false));
            }
            else if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive && !file.Local.IsDownloadingCompleted)
            {
                if (SettingsService.Current.IsStreamingEnabled && item.IsVideo && item.IsStreamable)
                {
                    _delegate?.OpenFile(item, file);
                }
                else
                {
                    item.ClientService.DownloadFile(file.Id, 32);
                }
            }
            else if (item.IsVideo)
            {
                _delegate?.OpenFile(item, file);
            }
        }

        private GalleryTransportControls _controls;

        private bool _stopped;

        private bool _unloaded;
        private int _fileId;

        public void Play(GalleryMedia item, double position, GalleryTransportControls controls)
        {
            if (_unloaded)
            {
                return;
            }

            try
            {
                var file = item.File;
                if (file.Id == _fileId || (!file.Local.IsDownloadingCompleted && !SettingsService.Current.IsStreamingEnabled))
                {
                    return;
                }

                _fileId = file.Id;

                // Always recreate HLS player for now, try to reuse native one
                if ((SettingsService.Current.Diagnostics.ForceWebView2 || item.IsHls()) && ChromiumWebPresenter.IsSupported())
                {
                    Video = new WebVideoPlayer();
                }
                else if (Video is not NativeVideoPlayer)
                {
                    Video = new NativeVideoPlayer();
                }

                controls.Attach(item, file);
                controls.Attach(Video);

                Video.Play(item, position);
                Button.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        public void Play(VideoPlayerBase player, GalleryMedia item, GalleryTransportControls controls)
        {
            if (_unloaded)
            {
                return;
            }

            try
            {
                var file = item.File;
                if (file.Id == _fileId || (!file.Local.IsDownloadingCompleted && !SettingsService.Current.IsStreamingEnabled))
                {
                    return;
                }

                _fileId = file.Id;

                Video = player;
                Button.Visibility = Visibility.Collapsed;
                //Video.IsUnloadedExpected = false;

                controls.Attach(item, file);
                controls.Attach(Video);
            }
            catch { }
        }

        public VideoPlayerBase Video
        {
            get => Panel.Child as VideoPlayerBase;
            set
            {
                var video = Panel.Child as VideoPlayerBase;
                if (video != null)
                {
                    video.TreeUpdated -= OnTreeUpdated;
                    video.FirstFrameReady -= OnFirstFrameReady;
                    video.Closed -= OnClosed;
                }

                if (value != null)
                {
                    value.TreeUpdated += OnTreeUpdated;
                    value.FirstFrameReady += OnFirstFrameReady;
                    value.Closed += OnClosed;
                }

                Panel.Child = value;
            }
        }

        public void Unload()
        {
            if (_unloaded)
            {
                return;
            }

            _unloaded = true;

            if (Video != null)
            {
                Video.Stop();
                Button.Visibility = Visibility.Visible;
            }

            UpdateManager.Unsubscribe(this, ref _fileToken);
            UpdateManager.Unsubscribe(this, ref _thumbnailToken, true);
        }

        private void OnTreeUpdated(VideoPlayerBase sender, EventArgs e)
        {
            // Hopefully this is always triggered after Unloaded/Loaded
            // And even if the events are raced and triggered in the opposite order
            // Not causing Disconnected/Connected to be triggered.
            sender.IsUnloadedExpected = false;
            sender.TreeUpdated -= OnTreeUpdated;
        }

        private void OnFirstFrameReady(VideoPlayerBase sender, EventArgs args)
        {
            MediaOpened();
        }

        private void OnClosed(VideoPlayerBase sender, EventArgs e)
        {
            if (_stopped)
            {
                _stopped = false;
                Video.Clear();
                Button.Visibility = Visibility.Visible;
            }
        }

        public void Stop(out int fileId, out double position)
        {
            if (Video != null && !_unloaded)
            {
                fileId = _fileId;
                position = Video.Position;

                _stopped = true;
                Video.Stop();
                Button.Visibility = Visibility.Visible;
            }
            else
            {
                fileId = 0;
                position = 0;
            }

            _fileId = 0;
        }
    }
}
