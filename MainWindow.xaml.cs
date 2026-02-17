using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using LibVLCSharp.Shared;

namespace Annotater;

public partial class MainWindow : Window
{
    private string? folderPath;
    private List<string> videoFiles = new();
    private int currentIndex = -1;
    private TimeSpan? time1;
    private TimeSpan? time2;
    private HashSet<string> processedFiles = new(StringComparer.OrdinalIgnoreCase);
    private ObservableCollection<VideoFileItem> fileItems = new();
    private bool isPlaying;
    private bool videoReachedEnd;
    private bool isSeeking;
    private bool userSelecting; // guard against recursive SelectionChanged
    private TimeSpan? pendingSeekPosition; // for resume from Continue.md
    private string lastPositionText = "";

    private LibVLC _libVLC = null!;
    private LibVLCSharp.Shared.MediaPlayer _mediaPlayer = null!;
    private long _duration; // ms

    public MainWindow()
    {
        InitializeComponent();
        FileListBox.ItemsSource = fileItems;

        _libVLC = new LibVLC();
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
        VideoView.MediaPlayer = _mediaPlayer;

        // LibVLC events fire on non-UI threads — marshal to dispatcher
        _mediaPlayer.LengthChanged += (s, e) =>
            Dispatcher.BeginInvoke(() => OnMediaLengthChanged(e.Length));

        _mediaPlayer.EndReached += (s, e) =>
            Dispatcher.BeginInvoke(() => OnMediaEnded());

        _mediaPlayer.TimeChanged += (s, e) =>
            Dispatcher.BeginInvoke(() => OnTimeChanged(e.Time));
    }

    // ── LibVLC event handlers (called on UI thread via dispatcher) ──

    private void OnMediaLengthChanged(long lengthMs)
    {
        _duration = lengthMs;
        SeekSlider.IsEnabled = true;
        SeekSlider.Maximum = lengthMs / 1000.0;
        LblDuration.Text = TimeSpan.FromMilliseconds(lengthMs).ToString(@"hh\:mm\:ss");

        if (pendingSeekPosition.HasValue)
        {
            _mediaPlayer.Time = (long)pendingSeekPosition.Value.TotalMilliseconds;
            pendingSeekPosition = null;
        }
    }

    private void OnMediaEnded()
    {
        isPlaying = false;
        videoReachedEnd = true;
    }

    private void OnTimeChanged(long timeMs)
    {
        var posText = TimeSpan.FromMilliseconds(timeMs).ToString(@"hh\:mm\:ss");

        if (posText != lastPositionText)
        {
            lastPositionText = posText;
            LblPosition.Text = posText;
            LblCurrentTime.Text = posText;
        }

        if (!isSeeking)
        {
            SeekSlider.Value = timeMs / 1000.0;
        }
    }

    // ── Folder / File loading ──────────────────────────────────────

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку с видеофайлами"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadFolder(dialog.FolderName);
        }
    }

    private void LoadFolder(string path)
    {
        folderPath = path;
        Title = $"Video Annotater — {Path.GetFileName(path)}";

        // Scan for video files
        var extensions = new[] { ".mp4", ".avi" };
        videoFiles = Directory.GetFiles(path)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (videoFiles.Count == 0)
        {
            MessageBox.Show("В этой папке не найдено файлов .mp4 или .avi.",
                "Нет видео", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Load processed files from Anonce.md
        LoadProcessedFiles();

        // Populate file list
        RefreshFileList();

        // Check for Continue.md
        var continuePath = Path.Combine(folderPath, "Continue.md");
        if (File.Exists(continuePath))
        {
            var result = MessageBox.Show(
                "Найден файл Continue.md. Продолжить с места остановки?",
                "Возобновление сессии", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                ResumeFromContinue(continuePath);
                return;
            }
            else
            {
                File.Delete(continuePath);
            }
        }

        currentIndex = -1;
        NoVideoLabel.Text = "Нажмите START (S) для начала";
        BtnStart.IsEnabled = true;
        BtnBegin.IsEnabled = false;
        BtnEnd.IsEnabled = false;
        BtnNext.IsEnabled = false;
        UpdateFileCount();
    }

    private void LoadProcessedFiles()
    {
        processedFiles.Clear();
        if (folderPath == null) return;

        var anoncePath = Path.Combine(folderPath, "Anonce.md");
        if (!File.Exists(anoncePath)) return;

        foreach (var line in File.ReadAllLines(anoncePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Format: "filename.mp4 Start HH:MM:SS, Stop HH:MM:SS."
            var spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var fileName = trimmed[..spaceIdx];
                processedFiles.Add(fileName);
            }
        }
    }

    private void RefreshFileList()
    {
        userSelecting = true;

        if (fileItems.Count != videoFiles.Count)
        {
            // Full rebuild only on folder load
            fileItems.Clear();
            for (int i = 0; i < videoFiles.Count; i++)
            {
                var name = Path.GetFileName(videoFiles[i]);
                fileItems.Add(new VideoFileItem
                {
                    Name = name,
                    FullPath = videoFiles[i],
                    IsProcessed = processedFiles.Contains(name),
                    IsCurrent = i == currentIndex
                });
            }
        }
        else
        {
            // Incremental update — only touch changed properties
            for (int i = 0; i < fileItems.Count; i++)
            {
                var item = fileItems[i];
                var shouldBeProcessed = processedFiles.Contains(item.Name);
                var shouldBeCurrent = i == currentIndex;

                if (item.IsProcessed != shouldBeProcessed)
                    item.IsProcessed = shouldBeProcessed;
                if (item.IsCurrent != shouldBeCurrent)
                    item.IsCurrent = shouldBeCurrent;
            }
        }

        if (currentIndex >= 0 && currentIndex < fileItems.Count)
            FileListBox.SelectedIndex = currentIndex;
        userSelecting = false;
    }

    // ── Continue / Resume ──────────────────────────────────────────

    private void ResumeFromContinue(string continuePath)
    {
        var lines = File.ReadAllLines(continuePath);
        if (lines.Length < 2) return;

        var fileName = lines[0].Trim();
        if (!TimeSpan.TryParse(lines[1].Trim(), out var position)) return;

        var idx = videoFiles.FindIndex(f =>
            string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));

        if (idx < 0)
        {
            MessageBox.Show($"Файл '{fileName}' из Continue.md не найден в папке.",
                "Ошибка возобновления", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        File.Delete(continuePath);
        pendingSeekPosition = position;
        PlayVideo(idx);
    }

    private void WriteContinue()
    {
        if (folderPath == null || currentIndex < 0) return;

        var continuePath = Path.Combine(folderPath, "Continue.md");
        var currentFileName = Path.GetFileName(videoFiles[currentIndex]);
        var position = TimeSpan.FromMilliseconds(_mediaPlayer.Time);

        File.WriteAllText(continuePath,
            $"{currentFileName}\n{position:hh\\:mm\\:ss}");
    }

    // ── Video playback ─────────────────────────────────────────────

    private void PlayVideo(int index)
    {
        if (index < 0 || index >= videoFiles.Count) return;

        currentIndex = index;
        videoReachedEnd = false;
        _duration = 0;
        lastPositionText = "";
        time1 = null;
        time2 = null;
        LblTime1.Text = "--:--:--";
        LblTime2.Text = "--:--:--";

        NoVideoLabel.Visibility = Visibility.Collapsed;
        VideoView.Visibility = Visibility.Visible;

        using var media = new Media(_libVLC, videoFiles[index], FromType.FromPath);
        _mediaPlayer.Play(media);
        isPlaying = true;

        BtnBegin.IsEnabled = true;
        BtnEnd.IsEnabled = false;
        BtnNext.IsEnabled = true;
        BtnStart.IsEnabled = false;

        RefreshFileList();
        UpdateFileCount();
    }

    private void SeekSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        isSeeking = true;
    }

    private void SeekSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        isSeeking = false;
        _mediaPlayer.Time = (long)(SeekSlider.Value * 1000);
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isSeeking)
        {
            LblCurrentTime.Text = TimeSpan.FromSeconds(SeekSlider.Value).ToString(@"hh\:mm\:ss");
        }
    }

    // ── Button handlers ────────────────────────────────────────────

    private void Start_Click(object sender, RoutedEventArgs e) => DoStart();
    private void Begin_Click(object sender, RoutedEventArgs e) => DoBegin();
    private void End_Click(object sender, RoutedEventArgs e) => DoEnd();
    private void Next_Click(object sender, RoutedEventArgs e) => DoNext();

    private void DoStart()
    {
        if (videoFiles.Count == 0) return;

        // Find first unprocessed video
        var idx = videoFiles.FindIndex(f =>
            !processedFiles.Contains(Path.GetFileName(f)));

        if (idx < 0)
        {
            MessageBox.Show("Все видео обработаны.",
                "Завершено", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PlayVideo(idx);
    }

    private TimeSpan GetCurrentPosition()
    {
        var timeMs = _mediaPlayer.Time;
        return timeMs < 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(timeMs);
    }

    private TimeSpan GetDuration()
    {
        return _duration > 0 ? TimeSpan.FromMilliseconds(_duration) : TimeSpan.Zero;
    }

    private void DoBegin()
    {
        if (currentIndex < 0) return;

        var pos = GetCurrentPosition() - TimeSpan.FromSeconds(3);
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        time1 = pos;
        LblTime1.Text = time1.Value.ToString(@"hh\:mm\:ss");
        BtnBegin.IsEnabled = false;
        BtnEnd.IsEnabled = true;
    }

    private void DoEnd()
    {
        if (currentIndex < 0 || time1 == null) return;

        var pos = GetCurrentPosition() + TimeSpan.FromSeconds(3);
        var dur = GetDuration();
        if (dur > TimeSpan.Zero && pos > dur)
            pos = dur;
        time2 = pos;
        LblTime2.Text = time2.Value.ToString(@"hh\:mm\:ss");

        // Pause video and show IN/OUT input overlay
        _mediaPlayer.SetPause(true);
        isPlaying = false;

        TxtInValue.Text = "";
        TxtOutValue.Text = "";
        InOutOverlay.Visibility = Visibility.Visible;
        TxtInValue.Focus();
    }

    private void InOutOk_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtInValue.Text, out var inValue) ||
            !int.TryParse(TxtOutValue.Text, out var outValue))
        {
            MessageBox.Show("Введите корректные целочисленные значения для IN и OUT.",
                "Некорректный ввод", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InOutOverlay.Visibility = Visibility.Collapsed;

        WriteAnnotation(inValue, outValue);

        // Mark as processed
        var fileName = Path.GetFileName(videoFiles[currentIndex]);
        processedFiles.Add(fileName);
        RefreshFileList();
        UpdateFileCount();

        BtnBegin.IsEnabled = true;
        BtnEnd.IsEnabled = false;

        // Resume playback
        _mediaPlayer.SetPause(false);
        isPlaying = true;
    }

    private void InOutCancel_Click(object sender, RoutedEventArgs e)
    {
        InOutOverlay.Visibility = Visibility.Collapsed;
        time2 = null;
        LblTime2.Text = "--:--:--";

        // Resume playback
        _mediaPlayer.SetPause(false);
        isPlaying = true;
    }

    private void IntTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private void DoNext()
    {
        if (videoFiles.Count == 0) return;

        // Find next unprocessed video after current
        for (int i = currentIndex + 1; i < videoFiles.Count; i++)
        {
            if (!processedFiles.Contains(Path.GetFileName(videoFiles[i])))
            {
                PlayVideo(i);
                return;
            }
        }

        // Wrap around from beginning
        for (int i = 0; i < currentIndex; i++)
        {
            if (!processedFiles.Contains(Path.GetFileName(videoFiles[i])))
            {
                PlayVideo(i);
                return;
            }
        }

        MessageBox.Show("All videos have been processed.",
            "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Speed controls ─────────────────────────────────────────────

    private void Speed1_Click(object sender, RoutedEventArgs e) => SetSpeed(1);
    private void Speed2_Click(object sender, RoutedEventArgs e) => SetSpeed(2);
    private void Speed3_Click(object sender, RoutedEventArgs e) => SetSpeed(3);
    private void Speed4_Click(object sender, RoutedEventArgs e) => SetSpeed(4);

    private void SetSpeed(int speed)
    {
        _mediaPlayer.SetRate(speed);

        var normalStyle = (Style)FindResource("SpeedButton");
        var activeStyle = (Style)FindResource("SpeedButtonActive");

        BtnSpeed1.Style = speed == 1 ? activeStyle : normalStyle;
        BtnSpeed2.Style = speed == 2 ? activeStyle : normalStyle;
        BtnSpeed3.Style = speed == 3 ? activeStyle : normalStyle;
        BtnSpeed4.Style = speed == 4 ? activeStyle : normalStyle;
    }

    // ── File list interaction ──────────────────────────────────────

    private void FileListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (userSelecting) return;
        if (FileListBox.SelectedIndex < 0) return;

        var item = fileItems[FileListBox.SelectedIndex];
        if (item.IsProcessed)
        {
            MessageBox.Show(
                $"'{item.Name}' уже обработан.\nУдалите строку из Anonce.md для повторной обработки.",
                "Уже обработан", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Restore selection
            userSelecting = true;
            if (currentIndex >= 0)
                FileListBox.SelectedIndex = currentIndex;
            userSelecting = false;
            return;
        }

        PlayVideo(FileListBox.SelectedIndex);
    }

    // ── Keyboard shortcuts ─────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Don't handle if a text input has focus
        if (e.OriginalSource is System.Windows.Controls.TextBox) return;

        switch (e.Key)
        {
            case Key.S:
                if (BtnStart.IsEnabled) DoStart();
                e.Handled = true;
                break;
            case Key.Q:
                if (BtnBegin.IsEnabled) DoBegin();
                e.Handled = true;
                break;
            case Key.W:
                if (BtnEnd.IsEnabled) DoEnd();
                e.Handled = true;
                break;
            case Key.N:
                if (BtnNext.IsEnabled) DoNext();
                e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                SetSpeed(1);
                e.Handled = true;
                break;
            case Key.D2:
            case Key.NumPad2:
                SetSpeed(2);
                e.Handled = true;
                break;
            case Key.D3:
            case Key.NumPad3:
                SetSpeed(3);
                e.Handled = true;
                break;
            case Key.D4:
            case Key.NumPad4:
                SetSpeed(4);
                e.Handled = true;
                break;
            case Key.Space:
                TogglePlayPause();
                e.Handled = true;
                break;
            case Key.O:
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    OpenFolder_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                break;
        }
    }

    private void TogglePlayPause()
    {
        if (currentIndex < 0) return;

        if (isPlaying)
        {
            _mediaPlayer.SetPause(true);
            isPlaying = false;
        }
        else
        {
            _mediaPlayer.SetPause(false);
            isPlaying = true;
        }
    }

    // ── Annotation writing ─────────────────────────────────────────

    private void WriteAnnotation(int inValue, int outValue)
    {
        if (folderPath == null || currentIndex < 0 || time1 == null || time2 == null) return;

        var fileName = Path.GetFileName(videoFiles[currentIndex]);
        var line = $"{fileName} Start {time1.Value:hh\\:mm\\:ss}, Stop {time2.Value:hh\\:mm\\:ss}. IN = {inValue}, OUT = {outValue}";

        var anoncePath = Path.Combine(folderPath, "Anonce.md");
        File.AppendAllText(anoncePath, line + Environment.NewLine);
    }

    // ── Window close ───────────────────────────────────────────────

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (folderPath != null && videoFiles.Count > 0)
        {
            bool hasUnprocessedFiles = videoFiles.Any(f =>
                !processedFiles.Contains(Path.GetFileName(f)));
            bool videoNotFinished = currentIndex >= 0 && !videoReachedEnd;

            if (hasUnprocessedFiles || videoNotFinished)
            {
                var result = MessageBox.Show(
                    "Есть необработанные видео. Сохранить прогресс в Continue.md?",
                    "Сохранение прогресса", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == MessageBoxResult.Yes)
                {
                    // If no video is currently loaded, save the first unprocessed file
                    if (currentIndex < 0)
                    {
                        var firstUnprocessed = videoFiles.FirstOrDefault(f =>
                            !processedFiles.Contains(Path.GetFileName(f)));
                        if (firstUnprocessed != null)
                        {
                            var continuePath = Path.Combine(folderPath, "Continue.md");
                            File.WriteAllText(continuePath,
                                $"{Path.GetFileName(firstUnprocessed)}\n00:00:00");
                        }
                    }
                    else
                    {
                        WriteContinue();
                    }
                }
            }
        }

        _mediaPlayer.Stop();
        VideoView.Dispose();
        _mediaPlayer.Dispose();
        _libVLC.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────

    private void UpdateFileCount()
    {
        var total = videoFiles.Count;
        var done = 0;
        foreach (var f in videoFiles)
        {
            if (processedFiles.Contains(Path.GetFileName(f)))
                done++;
        }
        LblFileCount.Text = $"{done}/{total}";
    }
}

// ── View model for file list items ─────────────────────────────

public class VideoFileItem : INotifyPropertyChanged
{
    private string name = "";
    private string fullPath = "";
    private bool isProcessed;
    private bool isCurrent;

    public string Name
    {
        get => name;
        set { name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string FullPath
    {
        get => fullPath;
        set { fullPath = value; OnPropertyChanged(nameof(FullPath)); }
    }

    public bool IsProcessed
    {
        get => isProcessed;
        set { isProcessed = value; OnPropertyChanged(nameof(IsProcessed)); }
    }

    public bool IsCurrent
    {
        get => isCurrent;
        set { isCurrent = value; OnPropertyChanged(nameof(IsCurrent)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
