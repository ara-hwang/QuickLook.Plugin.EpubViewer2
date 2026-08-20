# QuickLook.Plugin.EpubViewer2

QuickLook(Windows)에서 `Space`로 `.epub` 전자책을 즉시 미리보기. WebView2 + VersOne.Epub 기반.

이 프로젝트는 [QL-Win/QuickLook.Plugin.EpubViewer](https://github.com/QL-Win/QuickLook.Plugin.EpubViewer)를 기반으로 제작되었습니다.

- WebView2(Chromium)로 EPUB 2/3 렌더링, CSS/이미지/폰트 완전 지원
- 커버 페이지 + 챕터 네비게이션 (←/→, Home/End)
- `file://quicklook/epub/*` 가상 호스트로 메모리에서 리소스 서빙

## 설치

1. `QuickLook.Plugin.EpubViewer2.qlplugin` 다운로드 (Releases 또는 `Scripts/pack-zip.ps1` 빌드)
2. `.qlplugin` 선택 후 `Space` → Install → QuickLook 재시작

## 빌드

```powershell
dotnet build QuickLook.Plugin.EpubViewer2/QuickLook.Plugin.EpubViewer2.csproj -c Release
powershell -ExecutionPolicy Bypass -File Scripts/pack-zip.ps1  # -> .qlplugin
```

요구 사항: Windows 10/11, WebView2 Runtime, .NET Framework 4.6.2

기본 플러그인 버전은 `1`입니다. 버전 파일을 다시 생성하려면 다음을 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File Scripts/update-version.ps1 -Version 1
```

`main` 브랜치와 Pull Request는 GitHub Actions에서 자동으로 빌드됩니다. `v1`처럼
`v`로 시작하는 태그를 푸시하면 해당 태그 번호로 빌드하고 `.qlplugin` GitHub Release도
자동 생성합니다.

## 라이선스

GPL-3.0 — [LICENSE](LICENSE)
