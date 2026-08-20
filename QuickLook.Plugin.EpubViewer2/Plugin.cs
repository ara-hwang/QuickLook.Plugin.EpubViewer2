// Copyright © 2026 QuickLook EpubViewer Contributors
//
// This file is part of QuickLook program.
// GPL-3.0 licensed. See LICENSE for details.

using QuickLook.Common.Plugin;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VersOne.Epub;
using VersOne.Epub.Options;

namespace QuickLook.Plugin.EpubViewer2;

public class Plugin : IViewer
{
    private EpubViewerPanel _panel;

    public int Priority => 0;

    public void Init()
    {
        // WebView2 runtime 확인은 패널에서 수행
    }

    public bool CanHandle(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
            return false;

        return string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase);
    }

    public void Prepare(string path, ContextObject context)
    {
        context.SetPreferredSizeFit(new Size { Width = 1000, Height = 800 }, 0.85);
        context.Title = Path.GetFileName(path);
        context.IsBusy = true;
    }

    public void View(string path, ContextObject context)
    {
        Exception exception = null;
        try
        {
            _panel = new EpubViewerPanel(context);
            context.ViewerContent = _panel;
        }
        catch (Exception ex)
        {
            exception = ex;
            LogError(ex, path);
            context.ViewerContent = CreateErrorPanel(ex, path);
            context.IsBusy = false;
            Debug.WriteLine($"EpubViewer panel creation failed: {ex}");
            return;
        }

        try
        {
            // 파일 IO는 백그라운드에서 수행 — WebView2 UI 작업만 Dispatcher로
            EpubBook book = null;
            try
            {
                book = TryReadEpub(path);
            }
            catch (Exception ex)
            {
                exception = ex;
                LogError(ex, path);
            }

            if (exception == null && book != null)
            {
                // UI 스레드에서 패널에 설정
                _panel.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        context.Title = string.IsNullOrWhiteSpace(book.Title)
                            ? Path.GetFileName(path)
                            : book.Title;

                        _panel.SetContent(book);
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                        LogError(ex, path);
                        context.ViewerContent = CreateErrorPanel(ex, path);
                    }
                    finally
                    {
                        context.IsBusy = false;
                    }
                }, DispatcherPriority.Loaded);
            }
            else
            {
                _panel.Dispatcher.Invoke(() =>
                {
                    context.ViewerContent = CreateErrorPanel(exception, path);
                    context.IsBusy = false;
                });
            }
        }
        catch (Exception ex)
        {
            exception = ex;
            LogError(ex, path);
            try
            {
                _panel.Dispatcher.Invoke(() =>
                {
                    context.ViewerContent = CreateErrorPanel(ex, path);
                    context.IsBusy = false;
                });
            }
            catch { }
        }

        // QuickLook 호스트가 예외를 로그로 남기도록 하되, UI는 이미 에러 패널 표시
        // 치명적이지 않으면 throw하지 않음 — 호스트 크래시 방지
        if (exception != null)
        {
            Debug.WriteLine($"EpubViewer View failed: {exception}");
            // 필요 시 주석 해제하여 호스트에 예외 전파
            // ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private static EpubBook TryReadEpub(string path)
    {
        // 1) 가장 관대하게 시도 (NAV 누락 등 무시)
        try
        {
            return EpubReader.ReadBook(path, EpubReaderOptionsPreset.IGNORE_ALL_ERRORS);
        }
        catch (Exception ex1)
        {
            Debug.WriteLine($"IGNORE_ALL_ERRORS failed: {ex1.Message}");
            // 2) RELAXED
            try
            {
                return EpubReader.ReadBook(path, EpubReaderOptionsPreset.RELAXED);
            }
            catch (Exception ex2)
            {
                Debug.WriteLine($"RELAXED failed: {ex2.Message}");
                // 3) 커스텀: NAV 누락만 명시적으로 무시하는 세밀 설정
                try
                {
                    var opts = new EpubReaderOptions();
                    opts.Epub3NavDocumentReaderOptions.IgnoreMissingNavManifestItemError = true;
                    opts.Epub3NavDocumentReaderOptions.IgnoreMissingNavFileError = true;
                    opts.Epub3NavDocumentReaderOptions.IgnoreNavFileIsNotValidXmlError = true;
                    opts.Epub3NavDocumentReaderOptions.IgnoreMissingHtmlElementError = true;
                    opts.Epub3NavDocumentReaderOptions.IgnoreMissingBodyElementError = true;
                    opts.ContentReaderOptions.IgnoreMissingFileError = true;
                    opts.ContentReaderOptions.IgnoreFileIsTooLargeError = true;
                    opts.PackageReaderOptions.IgnoreMissingToc = true;
                    opts.PackageReaderOptions.SkipInvalidManifestItems = true;
                    return EpubReader.ReadBook(path, opts);
                }
                catch (Exception ex3)
                {
                    Debug.WriteLine($"Custom lenient failed: {ex3.Message}");
                    // 마지막으로 원본 예외를 던져 상위에서 처리
                    throw new AggregateException($"EPUB 파싱 실패 (3단계 시도 모두 실패). 마지막 오류: {ex3.Message}", ex1, ex2, ex3);
                }
            }
        }
    }

    private static void LogError(Exception ex, string path)
    {
        try
        {
            var logDir = Path.Combine(Path.GetTempPath(), "QuickLook.EpubViewer");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "error.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Path: {path}\r\n{ex}\r\n\r\n");
        }
        catch { }
    }

    public void Cleanup()
    {
        GC.SuppressFinalize(this);
        try
        {
            _panel?.Dispose();
        }
        catch { }
        _panel = null;
    }

    private static object CreateErrorPanel(Exception e, string path)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "QuickLook.EpubViewer", "error.log");
        var detail = e?.ToString() ?? "알 수 없는 오류";
        // 메시지 너무 길면 요약 + 로그 안내
        var text = $"EPUB 파일을 열 수 없습니다.\n\n{Path.GetFileName(path)}\n\n{detail}\n\n로그: {logPath}";
        var scroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20),
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 12
            }
        };
        return scroll;
    }
}
