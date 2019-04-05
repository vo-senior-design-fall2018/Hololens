using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using UnityEngine;
using System.Linq;
using UnityEngine.UI;

#if ENABLE_WINMD_SUPPORT
using Windows.Media.Capture.Frames;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Media.MediaProperties;
using Windows.Graphics.Imaging;
using Windows.UI.Core;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Devices;
using Windows.Storage;
using Windows.Storage.Streams;
#endif

public class RGBDGrabber : MonoBehaviour
{
#if ENABLE_WINMD_SUPPORT
    Text errorMessage;

    public void Tapped()
    {
        LogError("tapped");
    }

    // Start is called before the first frame update
    private void Start()
    {
        GameObject text = GameObject.Find("Text");
        errorMessage = text.GetComponent<Text>();

        init();
    }

    // Update is called once per frame
    private void Update()
    {
        
    }

    private void OnApplicationQuitAsync()
    {
        deinit();
    }

    private void LogError(string message)
    {
        errorMessage.text = message;
        System.Diagnostics.Debug.WriteLine(message);
    }

    MediaCapture mediaCapture;

    private MultiSourceMediaFrameReader _frameReader;
    private string _colorSourceId;
    private string _depthSourceId;

    private SoftwareBitmap backBuffer;
    private bool taskRunning = false;
    private bool capturing = true;

    private readonly ManualResetEventSlim _frameReceived = new ManualResetEventSlim(false);
    private readonly CancellationTokenSource _tokenSource = new CancellationTokenSource();
    public event EventHandler CorrelationFailed;

    private async void init()
    {
        var frameSourceGroups = await MediaFrameSourceGroup.FindAllAsync();
        LogError("checkpoint 1.1");
        var targetGroups = frameSourceGroups.Select(g => new
        {
            Group = g,
            SourceInfos = new MediaFrameSourceInfo[]
            {
                g.SourceInfos.FirstOrDefault(info => info.SourceKind == MediaFrameSourceKind.Color),
                g.SourceInfos.FirstOrDefault(info => info.SourceKind == MediaFrameSourceKind.Depth),
            }
        }).Where(g => g.SourceInfos.Any(info => info != null)).ToList();
        LogError("checkpoint 1.2");
        
        if (targetGroups.Count == 0)
        {
            LogError("No source groups found.");
            return;
        }

        MediaFrameSourceGroup mediaSourceGroup = targetGroups[0].Group;
        LogError("checkpoint 1.3");

        mediaCapture = new MediaCapture();

        LogError("checkpoint 1.4");
        var settings = new MediaCaptureInitializationSettings()
        {
            SourceGroup = mediaSourceGroup,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video
        };
        LogError("checkpoint 1.5");

        await mediaCapture.InitializeAsync(settings);
        LogError("checkpoint 1.6");

        MediaFrameSource colorSource = 
            mediaCapture.FrameSources.Values.FirstOrDefault(
                s => s.Info.SourceKind == MediaFrameSourceKind.Color);

        MediaFrameSource depthSource =
            mediaCapture.FrameSources.Values.FirstOrDefault(
                s => s.Info.SourceKind == MediaFrameSourceKind.Depth);
        LogError("checkpoint 1.7");

        if (colorSource == null || depthSource == null)
        {
            LogError("Cannot find color or depth stream.");
            return;
        }

        MediaFrameFormat colorFormat = colorSource.SupportedFormats.Where(format =>
        {
            return format.VideoFormat.Width >= 640
                && format.Subtype == MediaEncodingSubtypes.Rgb24;
        }).FirstOrDefault();

        MediaFrameFormat depthFormat = depthSource.SupportedFormats.Where(format =>
        {
            return format.VideoFormat.Width >= 640
                && format.Subtype == MediaEncodingSubtypes.D16;
        }).FirstOrDefault();

        await colorSource.SetFormatAsync(colorFormat);
        await depthSource.SetFormatAsync(depthFormat);

        _colorSourceId = colorSource.Info.Id;
        _depthSourceId = depthSource.Info.Id;

        _frameReader = await mediaCapture.CreateMultiSourceFrameReaderAsync(
            new[] { colorSource, depthSource });

        _frameReader.FrameArrived += FrameReader_FrameArrived;

        MultiSourceMediaFrameReaderStartStatus startStatus = await _frameReader.StartAsync();
        
        if (startStatus != MultiSourceMediaFrameReaderStartStatus.Success)
        {
            throw new InvalidOperationException("Unable to start reader: " + startStatus);
        }

        this.CorrelationFailed += MainPage_CorrelationFailed;
        Task.Run(() => NotifyAboutCorrelationFailure(_tokenSource.Token));
    }

    private async void deinit()
    {
        await _frameReader.StopAsync();
        _frameReader.FrameArrived -= FrameReader_FrameArrived;
        mediaCapture.Dispose();
        mediaCapture = null;
    }
    
    private async void FrameReader_FrameArrived(MultiSourceMediaFrameReader sender, MultiSourceMediaFrameArrivedEventArgs args)
    {
        if (capturing)
        {
            capturing = false;

            using (MultiSourceMediaFrameReference muxedFrameRef = sender.TryAcquireLatestFrame())
            using (MediaFrameReference colorFrameRef = muxedFrameRef.TryGetFrameReferenceBySourceId(_colorSourceId))
            using (MediaFrameReference depthFrameRef = muxedFrameRef.TryGetFrameReferenceBySourceId(_depthSourceId))
            {
                _frameReceived.Set();
                // do something with the frames

                VideoMediaFrame colorFrame = colorFrameRef.VideoMediaFrame;
                VideoMediaFrame depthFrame = depthFrameRef.VideoMediaFrame;
                SoftwareBitmap colorBitmap = colorFrame?.SoftwareBitmap;
                SoftwareBitmap depthBitmap = depthFrame?.SoftwareBitmap;

                StorageFolder storageFolder = ApplicationData.Current.LocalFolder;
                StorageFile outputFile = await storageFolder.CreateFileAsync("image.png", CreationCollisionOption.ReplaceExisting);

                SaveSoftwareBitmapToFile(colorBitmap, outputFile);

                colorBitmap.Dispose();
                depthBitmap.Dispose();
            }

        }
    }

    private async void SaveSoftwareBitmapToFile(SoftwareBitmap softwareBitmap, StorageFile outputFile)
    {
        using (IRandomAccessStream stream = await outputFile.OpenAsync(FileAccessMode.ReadWrite))
        {
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            encoder.SetSoftwareBitmap(softwareBitmap);

            encoder.BitmapTransform.ScaledWidth = 640;
            encoder.BitmapTransform.ScaledHeight = 480;
            encoder.IsThumbnailGenerated = true;

            try
            {
                await encoder.FlushAsync();
            }
            catch (Exception e)
            {
                LogError("Error Flushing SoftwareBitMap: " + e.Message);

                const int WINCODEC_ERR_UNSUPPORTEDOPERATION = unchecked((int)0x88982F81);
                switch (e.HResult)
                {
                    case WINCODEC_ERR_UNSUPPORTEDOPERATION:
                        encoder.IsThumbnailGenerated = false;
                        break;
                    default:
                        throw;
                }
            }

            if (encoder.IsThumbnailGenerated == false)
            {
                await encoder.FlushAsync();
            }
        }
    }

    private void NotifyAboutCorrelationFailure(CancellationToken token)
    {
        if (WaitHandle.WaitAny(new[] { token.WaitHandle, _frameReceived.WaitHandle }, 5000)
            == WaitHandle.WaitTimeout)
        {
            CorrelationFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void MainPage_CorrelationFailed(object sender, EventArgs args)
    {
        deinit();
    }
#endif
}
