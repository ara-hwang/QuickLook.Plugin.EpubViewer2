// Copyright © 2026 QuickLook EpubViewer Contributors
// GPL-3.0

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VersOne.Epub;

namespace QuickLook.Plugin.EpubViewer2;

public partial class EpubViewerPanel : UserControl, IDisposable
{
    private readonly ContextObject _context;
    private WebView2 _webView;
    private EpubBook _book;
    private List<EpubLocalTextContentFile> _readingOrder;
    private Dictionary<string, string> _chapterTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private int _currentIndex = -1; // -1 = cover
    private DateTime _lastWheelTime = DateTime.MinValue;
    private const int WheelDebounceMs = 350;
    private bool _isHandlingWheel;
    private bool _initializationStarted;
    private bool _isDisposed;
    private bool _isTocVisible = true;
    private bool _isUpdatingTocSelection = false;
    private GridLength _sidebarLastWidth = new GridLength(240);

    public EpubViewerPanel(ContextObject context)
    {
        _context = context;
        InitializeComponent();

        DataContext = this;

        buttonPrevChapter.Click += (s, e) => PrevChapter();
        buttonNextChapter.Click += (s, e) => NextChapter();
        toggleTocButton.Click += (s, e) => ToggleToc();
        closeTocButton.Click += (s, e) => HideToc();
        tocListBox.SelectionChanged += TocListBox_SelectionChanged;

        // 초기 버튼 상태
        UpdateChrome();

        // WebView2 초기화
        InitializeWebView();
    }

    private void InitializeWebView()
    {
        if (!IsWebView2Available())
        {
            webViewHost.Content = CreateDownloadButton();
            buttonPrevChapter.Visibility = Visibility.Collapsed;
            buttonNextChapter.Visibility = Visibility.Collapsed;
            pageInfoBorder.Visibility = Visibility.Collapsed;
            return;
        }

        loadingText.Visibility = Visibility.Visible;

        string userDataFolder = null;
        try
        {
            userDataFolder = Path.Combine(SettingHelper.LocalDataPath, @"WebView2_Data\EpubViewer");
        }
        catch
        {
            userDataFolder = Path.Combine(Path.GetTempPath(), "QuickLook.EpubViewer", "WebView2_Data");
        }
        try { Directory.CreateDirectory(userDataFolder); } catch { }

        _webView = new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder,
            },
            DefaultBackgroundColor = System.Drawing.Color.White,
        };

        _webView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;
        _webView.NavigationCompleted += WebView_NavigationCompleted;
        _webView.PreviewMouseWheel += RootGrid_PreviewMouseWheel;

        webViewHost.Content = _webView;
        StartWebViewInitialization();

        // 포커스 가능하도록 — WebView2가 포커스를 가져가도 키 입력 보장
        Focusable = true;
        IsTabStop = true;
        Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(rootGrid)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void StartWebViewInitialization()
    {
        if (_initializationStarted || _isDisposed || _webView == null)
            return;

        _initializationStarted = true;
        try
        {
            await _webView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            ShowWebViewInitializationError(ex);
        }
    }

    private void ShowWebViewInitializationError(Exception exception)
    {
        if (_isDisposed)
            return;

        Debug.WriteLine($"WebView2 init failed: {exception}");
        loadingText.Text = "WebView2 초기화 실패";
        loadingText.Visibility = Visibility.Visible;
        _context.IsBusy = false;
    }

    private static bool IsWebView2Available()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    private object CreateDownloadButton()
    {
        var button = new Button
        {
            Content = "WebView2 Runtime이 필요합니다. 클릭하여 다운로드",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(20, 6, 20, 6)
        };
        button.Click += (s, e) => Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        return button;
    }

    internal void SetContent(EpubBook book)
    {
        _book = book ?? throw new ArgumentNullException(nameof(book));
        _readingOrder = book.ReadingOrder?.ToList() ?? new List<EpubLocalTextContentFile>();

        // ReadingOrder가 비어있으면 Html 컬렉션으로 폴백
        if (_readingOrder.Count == 0)
        {
            _readingOrder = book.Content?.Html?.Local?.ToList() ?? new List<EpubLocalTextContentFile>();
        }

        BuildChapterTitleMap();

        _currentIndex = -1; // 표지부터
        PopulateToc();
        UpdateChrome();

        if (_webView == null)
            return;

        if (_webView.CoreWebView2 != null)
        {
            NavigateToCurrent();
        }
    }

    private void NavigateToCurrent()
    {
        if (_isDisposed || _webView?.CoreWebView2 == null || _book == null)
            return;

        int total = _readingOrder?.Count ?? 0;
        if (_currentIndex < -1 || _currentIndex >= total)
            _currentIndex = total == 0 ? -1 : Math.Min(Math.Max(_currentIndex, -1), total - 1);

        string url;
        if (_currentIndex == -1)
        {
            url = "file://quicklook/epub/cover";
        }
        else
        {
            var file = _readingOrder[_currentIndex];
            var encodedPath = string.Join("/", file.FilePath.Split('/').Select(Uri.EscapeDataString));
            url = $"file://quicklook/epub/{encodedPath}";
        }

        try
        {
            _webView.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Navigate failed: {ex}");
        }

        UpdateChrome();
    }

    private void UpdateChrome()
    {
        if (_book == null)
        {
            pageInfoBorder.Visibility = Visibility.Collapsed;
            buttonPrevChapter.IsEnabled = false;
            buttonNextChapter.IsEnabled = false;
            return;
        }

        int total = _readingOrder?.Count ?? 0;

        if (_currentIndex == -1)
        {
            _context.Title = string.IsNullOrWhiteSpace(_book.Title) ? "표지" : _book.Title;
            pageInfoText.Text = total == 0 ? "Cover" : $"Cover (1/{total + 1})";
            pageInfoBorder.Visibility = Visibility.Visible;
        }
        else
        {
            var file = _readingOrder[_currentIndex];
            // Navigation title이 있으면 사용, 없으면 파일명
            string chapterTitle = TryGetChapterTitle(_currentIndex) ?? Path.GetFileNameWithoutExtension(file.FilePath);
            string bookTitle = string.IsNullOrWhiteSpace(_book.Title) ? "EPUB" : _book.Title;
            _context.Title = $"{bookTitle} - {chapterTitle} ({_currentIndex + 1}/{total})";
            pageInfoText.Text = $"{chapterTitle} ({_currentIndex + 1}/{total})";
            pageInfoBorder.Visibility = Visibility.Visible;
        }

        buttonPrevChapter.IsEnabled = _currentIndex >= 0;
        buttonNextChapter.IsEnabled = _currentIndex < total - 1 || _currentIndex == -1 && total > 0;

        // 사이드바 TOC 선택 동기화 (마크다운 뷰어처럼)
        try
        {
            _isUpdatingTocSelection = true;
            int tocIndex = _currentIndex + 1; // 0 = 표지
            if (tocIndex >= 0 && tocIndex < tocListBox.Items.Count)
            {
                tocListBox.SelectedIndex = tocIndex;
                // 선택 항목이 보이도록 스크롤
                var item = tocListBox.SelectedItem;
                if (item != null) tocListBox.ScrollIntoView(item);
            }
        }
        finally
        {
            _isUpdatingTocSelection = false;
        }

        // 툴팁 업데이트
        buttonPrevChapter.ToolTip = "이전 챕터 (←)";
        buttonNextChapter.ToolTip = "다음 챕터 (→)";
    }

    private void PopulateToc()
    {
        try
        {
            _isUpdatingTocSelection = true;
            tocListBox.Items.Clear();

            // 표지
            tocListBox.Items.Add(new TocItem { Title = "표지", Index = -1, Level = 0 });

            int total = _readingOrder?.Count ?? 0;
            for (int i = 0; i < total; i++)
            {
                string title = TryGetChapterTitle(i) ?? Path.GetFileNameWithoutExtension(_readingOrder[i].FilePath);
                tocListBox.Items.Add(new TocItem { Title = title, RawTitle = title, Index = i, Level = 0 });
            }

            tocListBox.SelectedIndex = 0;
            tocListBox.ScrollIntoView(tocListBox.Items[0]);

            // 사이드바 책 정보
            try
            {
                string author = string.Join(", ", _book.AuthorList ?? new List<string>());
                if (string.IsNullOrWhiteSpace(author)) author = _book.Author ?? "";
                sidebarBookInfo.Text = $"{_book.Title}\n{author}".Trim();
                if (string.IsNullOrWhiteSpace(sidebarBookInfo.Text)) sidebarBookInfo.Text = "EPUB";
            }
            catch { sidebarBookInfo.Text = _book.Title ?? "EPUB"; }
        }
        finally
        {
            _isUpdatingTocSelection = false;
        }
    }

    private void ToggleToc()
    {
        if (_isTocVisible) HideToc(); else ShowToc();
    }

    private void ShowToc()
    {
        sidebarPanel.Visibility = Visibility.Visible;
        sidebarColumn.Width = _sidebarLastWidth;
        _isTocVisible = true;
    }

    private void HideToc()
    {
        // 현재 너비 저장
        if (sidebarColumn.Width.Value > 0) _sidebarLastWidth = sidebarColumn.Width;
        sidebarColumn.Width = new GridLength(0);
        sidebarPanel.Visibility = Visibility.Collapsed;
        _isTocVisible = false;
    }

    private void TocListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTocSelection) return;
        if (tocListBox.SelectedItem is TocItem item)
        {
            _currentIndex = item.Index;
            NavigateToCurrent();
        }
    }

    private class TocItem
    {
        public string Title { get; set; }
        public string RawTitle { get; set; }
        public int Index { get; set; }
        public int Level { get; set; }
        public override string ToString() => Title;
    }

    private string TryGetChapterTitle(int index)
    {
        if (_readingOrder == null || index < 0 || index >= _readingOrder.Count)
            return null;

        string filePath = NormalizeEpubPath(_readingOrder[index].FilePath);
        if (_chapterTitles.TryGetValue(filePath, out string title))
            return title;

        return null;
    }

    private void BuildChapterTitleMap()
    {
        _chapterTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_book?.Navigation == null)
            return;

        foreach (EpubNavigationItem item in FlattenNavigation(_book.Navigation))
        {
            string filePath = NormalizeEpubPath(item.HtmlContentFile?.FilePath);
            if (!string.IsNullOrWhiteSpace(filePath) &&
                !string.IsNullOrWhiteSpace(item.Title) &&
                !_chapterTitles.ContainsKey(filePath))
            {
                _chapterTitles.Add(filePath, item.Title.Trim());
            }
        }
    }

    private static string NormalizeEpubPath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
    }

    private static IEnumerable<EpubNavigationItem> FlattenNavigation(IEnumerable<EpubNavigationItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item.NestedItems != null)
                foreach (var sub in FlattenNavigation(item.NestedItems))
                    yield return sub;
        }
    }

    private void PrevChapter()
    {
        if (_currentIndex >= 0)
        {
            _currentIndex--;
            NavigateToCurrent();
        }
    }

    private void NextChapter()
    {
        int total = _readingOrder?.Count ?? 0;
        if (_currentIndex < total - 1)
        {
            _currentIndex++;
            NavigateToCurrent();
        }
    }

    private void RootGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key))
            e.Handled = true;
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+L: 사이드바 토글 (마크다운 뷰어와 동일 단축키)
        if (e.Key == Key.L && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ToggleToc();
            e.Handled = true;
            return;
        }
        if (HandleKey(e.Key))
            e.Handled = true;
    }

    private bool HandleKey(Key key)
    {
        // 방향키 + PageUp/Down + Home/End 모두 챕터 이동으로 매핑 (Space는 QuickLook 닫기와 충돌하므로 제외)
        switch (key)
        {
            case Key.Left:
            case Key.Up:
            case Key.PageUp:
            case Key.BrowserBack:
                PrevChapter();
                return true;
            case Key.Right:
            case Key.Down:
            case Key.PageDown:
            case Key.BrowserForward:
                NextChapter();
                return true;
            case Key.Home:
                _currentIndex = -1;
                NavigateToCurrent();
                return true;
            case Key.End:
                _currentIndex = (_readingOrder?.Count ?? 1) - 1;
                NavigateToCurrent();
                return true;
            default:
                return false;
        }
    }

    private async void RootGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_book == null || _isHandlingWheel)
            return;

        bool isDown = e.Delta < 0;

        // Ctrl+Wheel은 확대/축소로 간주하여 챕터 이동 차단 (WebView2 기본 줌)
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            return;

        _isHandlingWheel = true;
        try
        {
            // 표지에서는 스크롤 위치와 무관하게 휠로 즉시 이동
            if (_currentIndex == -1)
            {
                if (isDown)
                    e.Handled = TryNavigateFromWheel(true);
                else
                    e.Handled = true;
                return;
            }

            bool shouldSwitch;
            if (_webView?.CoreWebView2 == null)
            {
                shouldSwitch = true;
            }
            else
            {
                shouldSwitch = await ShouldSwitchChapterOnWheelAsync(isDown);
            }

            if (shouldSwitch && !_isDisposed)
                e.Handled = TryNavigateFromWheel(isDown);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Mouse wheel handling failed: {ex}");
        }
        finally
        {
            _isHandlingWheel = false;
        }
    }

    private bool TryNavigateFromWheel(bool isDown)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastWheelTime).TotalMilliseconds < WheelDebounceMs)
            return false;

        int previousIndex = _currentIndex;
        if (isDown)
            NextChapter();
        else
            PrevChapter();

        if (_currentIndex == previousIndex)
            return false;

        _lastWheelTime = now;
        return true;
    }

    private async Task<bool> ShouldSwitchChapterOnWheelAsync(bool isDown)
    {
        // WebView2가 아직 준비 안 됐으면 무조건 전환
        if (_webView?.CoreWebView2 == null)
            return true;

        try
        {
            // JS로 스크롤 위치 조회. ExecuteScriptAsync는 JSON 문자열로 반환됨 ("true"/"false" 포함 따옴표)
            string script;
            if (isDown)
                script = "(function(){var sh=document.documentElement.scrollHeight, h=window.innerHeight, y=window.scrollY; if(sh<=h+5) return true; return (y+h) >= (sh-5);})()";
            else
                script = "(function(){var y=window.scrollY; if(document.documentElement.scrollHeight<=window.innerHeight+5) return true; return y<=5;})()";

            string result = await _webView.CoreWebView2.ExecuteScriptAsync(script);
            // result는 "\"true\"" 또는 "\"false\"" 또는 "true"
            result = result?.Trim().Trim('"');
            if (result == "true")
                return true;
            if (result == "false")
                return false;
            // 파싱 실패 시 기본값: 끝에 도달한 것으로 간주하여 전환
            return true;
        }
        catch
        {
            return true;
        }
    }

    private void WebView_CoreWebView2InitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowWebViewInitializationError(e.InitializationException);
            return;
        }

        loadingText.Visibility = Visibility.Collapsed;

        try
        {
            _webView.CoreWebView2.AddWebResourceRequestedFilter("file://quicklook/*", CoreWebView2WebResourceContext.All);
            _webView.CoreWebView2.WebResourceRequested += WebView_WebResourceRequested;
            _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            _webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            // 페이지 내 휠/클릭을 호스트로 전달하여 챕터 이동 (WPF Preview가 안 먹히는 WebView2 내부 대응)
            try
            {
                string wheelScript = @"
window.addEventListener('wheel', function(e){
  if(e.ctrlKey) return;
  var atTop = window.scrollY <= 5;
  var sh = document.documentElement.scrollHeight;
  var h = window.innerHeight;
  var y = window.scrollY;
  var atBottom = (y + h) >= (sh - 5);
  var isDown = e.deltaY > 0;
  var isCover = location.pathname === '/epub/cover';
  var shouldSwitch = false;
  if(isCover){ shouldSwitch = isDown; }
  else { if(sh <= h + 5) shouldSwitch = true; else shouldSwitch = isDown ? atBottom : atTop; }
  if(shouldSwitch){ chrome.webview.postMessage(isDown ? 'wheel-next' : 'wheel-prev'); e.preventDefault(); }
}, {passive:false});
window.addEventListener('click', function(e){
  if(location.pathname === '/epub/cover'){ chrome.webview.postMessage('cover-click'); }
});
";
                _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(wheelScript);
            }
            catch { }

            // 이미 SetContent 호출 후 대기 중이었다면 네비게이션
            if (_book != null)
                NavigateToCurrent();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void CoreWebView2_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs args)
    {
        Uri uri = null;
        bool isInternal = Uri.TryCreate(args.Uri, UriKind.Absolute, out uri) &&
                          uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) &&
                          uri.Host.Equals("quicklook", StringComparison.OrdinalIgnoreCase) &&
                          uri.AbsolutePath.StartsWith("/epub/", StringComparison.OrdinalIgnoreCase);
        if (isInternal)
        {
            UpdateCurrentIndexFromUri(uri);
            return;
        }

        args.Cancel = true;

        // 자동 리디렉션이나 EPUB 스크립트가 브라우저를 실행하지 못하도록 실제 사용자 탐색만 허용한다.
        if (args.IsUserInitiated && uri != null &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            try { Process.Start(uri.AbsoluteUri); } catch { }
        }
    }

    private void UpdateCurrentIndexFromUri(Uri uri)
    {
        string internalPath = NormalizeEpubPath(Uri.UnescapeDataString(uri.AbsolutePath.Substring("/epub/".Length)));
        int newIndex = internalPath.Equals("cover", StringComparison.OrdinalIgnoreCase)
            ? -1
            : _readingOrder?.FindIndex(file =>
                string.Equals(NormalizeEpubPath(file.FilePath), internalPath, StringComparison.OrdinalIgnoreCase)) ?? -1;

        if (newIndex == _currentIndex || (newIndex < 0 && !internalPath.Equals("cover", StringComparison.OrdinalIgnoreCase)))
            return;

        _currentIndex = newIndex;
        UpdateChrome();
    }

    private void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // 네비게이션 완료 후 포커스 복구 — 방향키가 최초 화면에서도 즉시 동작하도록
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                rootGrid.Focus();
                Keyboard.Focus(rootGrid);
            }
            catch { }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            string msg = args.TryGetWebMessageAsString();
            // JS에서 postMessage로 'wheel-next' / 'wheel-prev' / 'cover-click' 전달
            if (msg == "wheel-next" || msg == "cover-click")
            {
                TryNavigateFromWheel(true);
            }
            else if (msg == "wheel-prev")
            {
                TryNavigateFromWheel(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebMessageReceived error: {ex}");
        }
    }

    private void WebView_WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        // _book이 아직 없거나 WebView가 닫힌 경우 무시
        if (_book == null || _webView?.CoreWebView2 == null)
            return;
        if (_book.Content == null)
            return;
        try
        {
            var uri = new Uri(args.Request.Uri);
            if (!uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.Equals("quicklook", StringComparison.OrdinalIgnoreCase))
                return;

            string absPath = Uri.UnescapeDataString(uri.AbsolutePath);

            if (!absPath.StartsWith("/epub/", StringComparison.OrdinalIgnoreCase))
                return;

            string internalPath = absPath.Substring(6); // "/epub/" 제거
            // 앞의 "/" 제거 (혹시 남아있으면)
            if (internalPath.StartsWith("/")) internalPath = internalPath.Substring(1);

            // Cover 페이지
            if (internalPath.Equals("cover", StringComparison.OrdinalIgnoreCase))
            {
                string html;
                try { html = GenerateCoverHtml(); }
                catch (Exception ex) { html = $"<html><body>Cover error: {System.Net.WebUtility.HtmlEncode(ex.Message)}</body></html>"; }
                var bytes = Encoding.UTF8.GetBytes(html);
                var stream = new MemoryStream(bytes);
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream, 200, "OK", GetHtmlResponseHeaders());
                return;
            }

            // Html 파일 - 가독성을 위한 좌우 여백 주입
            if (_book.Content.Html.TryGetLocalFileByFilePath(internalPath, out var htmlFile))
            {
                string content = InjectReadingStyle(htmlFile.Content);
                var bytes = Encoding.UTF8.GetBytes(content);
                var stream = new MemoryStream(bytes);
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream, 200, "OK", GetHtmlResponseHeaders());
                return;
            }

            // Css
            if (_book.Content.Css.TryGetLocalFileByFilePath(internalPath, out var cssFile))
            {
                var bytes = Encoding.UTF8.GetBytes(cssFile.Content);
                var stream = new MemoryStream(bytes);
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream, 200, "OK", "Content-Type: text/css; charset=utf-8");
                return;
            }

            // Images
            if (_book.Content.Images.TryGetLocalFileByFilePath(internalPath, out var imgFile))
            {
                var mime = GetMimeType(Path.GetExtension(internalPath));
                var stream = new MemoryStream(imgFile.Content);
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream, 200, "OK", $"Content-Type: {mime}");
                return;
            }

            // Fonts
            if (_book.Content.Fonts.TryGetLocalFileByFilePath(internalPath, out var fontFile))
            {
                var mime = GetMimeType(Path.GetExtension(internalPath));
                var stream = new MemoryStream(fontFile.Content);
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream, 200, "OK", $"Content-Type: {mime}");
                return;
            }

            // 기타 모든 파일 (오디오 등)
            if (_book.Content.AllFiles.TryGetLocalFileByFilePath(internalPath, out var anyFile))
            {
                // AllFiles는 EpubLocalContentFile 타입, Content를 바이트로 읽어야 함
                // 리플렉션 없이 타입 체크: EpubLocalByteContentFile vs Text
                if (anyFile is EpubLocalByteContentFile byteFile)
                {
                    var mime = GetMimeType(Path.GetExtension(internalPath));
                    var stream = new MemoryStream(byteFile.Content);
                    args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        stream, 200, "OK", $"Content-Type: {mime}");
                    return;
                }
                else if (anyFile is EpubLocalTextContentFile textFile)
                {
                    var mime = GetMimeType(Path.GetExtension(internalPath));
                    string textContent = mime == "text/html" ? InjectReadingStyle(textFile.Content) : textFile.Content;
                    var bytes = Encoding.UTF8.GetBytes(textContent);
                    var stream = new MemoryStream(bytes);
                    args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        stream, 200, "OK", $"Content-Type: {mime}; charset=utf-8");
                    return;
                }
            }

            // 일부 EPUB은 경로 대소문자나 구분자가 일치하지 않는다. 파일명만 비교하면
            // 서로 다른 폴더의 동명 리소스를 잘못 반환하므로 전체 정규화 경로로만 보정한다.
            var matchingFile = _book.Content.AllFiles.Local.FirstOrDefault(file =>
                string.Equals(NormalizeEpubPath(file.FilePath), NormalizeEpubPath(internalPath), StringComparison.OrdinalIgnoreCase));
            if (matchingFile is EpubLocalByteContentFile matchingByteFile)
            {
                var stream = new MemoryStream(matchingByteFile.Content);
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream, 200, "OK", $"Content-Type: {GetMimeType(Path.GetExtension(internalPath))}");
                return;
            }
            if (matchingFile is EpubLocalTextContentFile matchingTextFile)
            {
                string matchingContent = matchingTextFile.Content;
                if (GetMimeType(Path.GetExtension(internalPath)) == "text/html")
                    matchingContent = InjectReadingStyle(matchingContent);
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(matchingContent));
                string mime = GetMimeType(Path.GetExtension(internalPath));
                string headers = mime == "text/html" ? GetHtmlResponseHeaders() : $"Content-Type: {mime}; charset=utf-8";
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
                return;
            }

            args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                Stream.Null, 404, "Not Found", "Content-Type: text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebResourceRequested error: {ex}");
        }
    }

    private static string GetHtmlResponseHeaders()
    {
        const string contentSecurityPolicy =
            "default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
            "font-src 'self' data:; media-src 'self' data:; object-src 'none'; frame-src 'none'; " +
            "connect-src 'none'; script-src 'none'; base-uri 'none'; form-action 'none'";
        return $"Content-Type: text/html; charset=utf-8\r\n" +
               $"Content-Security-Policy: {contentSecurityPolicy}\r\n" +
               "X-Content-Type-Options: nosniff";
    }

    private static string InjectReadingStyle(string html)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        // 이미 주입된 경우 중복 방지
        if (html.IndexOf("quicklook-epub-margin", StringComparison.OrdinalIgnoreCase) >= 0)
            return html;

        const string style = "<style id=\"quicklook-epub-margin\">html{background:#fff}body{padding:20px 24px !important;max-width:780px !important;margin:0 auto !important;box-sizing:border-box !important;overflow-wrap:break-word !important;word-break:break-word !important}img,svg,video{max-width:100% !important;height:auto !important}pre{white-space:pre-wrap !important;word-break:break-word !important}@media(max-width:600px){body{padding:14px 16px !important}}</style>";

        int idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return html.Insert(idx, style);

        idx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int end = html.IndexOf('>', idx);
            if (end >= 0)
                return html.Insert(end + 1, style);
        }

        idx = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int end = html.IndexOf('>', idx);
            if (end >= 0)
                return html.Insert(end + 1, "<head>" + style + "</head>");
        }

        // head/html 태그가 없는 비정형 문서
        return style + html;
    }

    private string GenerateCoverHtml()
    {
        var title = System.Net.WebUtility.HtmlEncode(_book.Title ?? "Untitled");
        var author = System.Net.WebUtility.HtmlEncode(string.Join(", ", _book.AuthorList ?? new List<string>()));
        if (string.IsNullOrWhiteSpace(author) && !string.IsNullOrWhiteSpace(_book.Author))
            author = System.Net.WebUtility.HtmlEncode(_book.Author);
        var description = System.Net.WebUtility.HtmlEncode(_book.Description ?? "");

        string coverImgTag = "";
        try
        {
            if (_book.CoverImage != null && _book.CoverImage.Length > 0)
            {
                // mime 추측: JPEG 기본
                string mime = "image/jpeg";
                // PNG 시그니처 체크
                if (_book.CoverImage.Length > 8 && _book.CoverImage[0] == 0x89 && _book.CoverImage[1] == 0x50)
                    mime = "image/png";
                string b64 = Convert.ToBase64String(_book.CoverImage);
                coverImgTag = $@"<img src=""data:{mime};base64,{b64}"" style=""max-height:55vh; max-width:85%; object-fit:contain; box-shadow:0 8px 24px rgba(0,0,0,0.2); border-radius:4px;"" />";
            }
            else if (_book.Content != null && _book.Content.Cover != null)
            {
                // Content.Cover가 있으면 해당 파일 경로로 이미지 요청 유도
                coverImgTag = $@"<div style=""width:300px; height:400px; background:#eee; display:flex; align-items:center; justify-content:center; color:#999;"">Cover</div>";
            }
        }
        catch { }

        string descHtml = string.IsNullOrWhiteSpace(description) ? "" : $@"<p style=""color:#666; font-size:13px; line-height:1.6; max-width:600px; margin:16px auto 0;"">{description}</p>";

        return $@"<!DOCTYPE html>
<html lang=""ko"">
<head>
<meta charset=""utf-8"" />
<meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
<style>
  html, body {{ margin:0; padding:0; height:100%; font-family:'Segoe UI', 'Malgun Gothic', sans-serif; background:#fafafa; color:#222; }}
  .wrap {{ min-height:100%; display:flex; flex-direction:column; align-items:center; justify-content:center; padding:40px 24px; box-sizing:border-box; text-align:center; }}
  h1 {{ font-size:26px; font-weight:600; margin:18px 0 6px; word-break:break-word; }}
  .author {{ font-size:14px; color:#666; margin:0; }}
  .hint {{ margin-top:24px; font-size:12px; color:#999; }}
</style>
</head>
<body>
  <div class=""wrap"">
    {coverImgTag}
    <h1>{title}</h1>
    {(string.IsNullOrWhiteSpace(author) ? "" : $@"<p class=""author"">{author}</p>")}
    {descHtml}
    <p class=""hint"">← → 방향키 또는 마우스 휠로 챕터를 이동할 수 있습니다</p>
  </div>
</body>
</html>";
    }

    private static string GetMimeType(string extension)
    {
        return extension?.ToLowerInvariant() switch
        {
            ".html" or ".htm" or ".xhtml" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".eot" => "application/vnd.ms-fontobject",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        try
        {
            if (_webView != null)
            {
                _webView.CoreWebView2InitializationCompleted -= WebView_CoreWebView2InitializationCompleted;
                _webView.NavigationCompleted -= WebView_NavigationCompleted;
                _webView.PreviewMouseWheel -= RootGrid_PreviewMouseWheel;
                try
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.WebResourceRequested -= WebView_WebResourceRequested;
                        _webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                        _webView.CoreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
                    }
                }
                catch { }
                _webView.Dispose();
                _webView = null;
            }
        }
        catch { }

        _book = null;
        _readingOrder = null;
        _chapterTitles.Clear();
        GC.SuppressFinalize(this);
    }
}
