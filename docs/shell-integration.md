# 7Zipper Windows 셸 통합

## 01 / 탐색기 메뉴 - 선택 항목 전달

- Windows 11: 파일·폴더 선택 → 오른쪽 클릭 → **더 많은 옵션 표시 → 7Zipper**.
- 압축: **7z로 압축**, **ZIP으로 압축**, **압축 옵션…**. 파일과 폴더 혼합 선택 가능.
- 풀기: **여기에 풀기**, **스마트 풀기**. 선택 항목이 모두 .7z, .zip, .7z.001 또는 ZIP 분할 볼륨(.z01 등)일 때 표시.
- 선택 경로 전체를 UTF-8 JSON으로 전달. 최대 요청 16 MiB. Explorer에서는 확장자만 확인하며 압축 파일을 읽지 않음.
- 실행 파일: 셸 DLL과 같은 폴더의 `7Zipper.exe`. 명령줄 인자는 `--shell-request <10진수 파이프 읽기 핸들>`. 선택 경로·암호는 명령줄에 넣지 않음.

| 항목 | 등록값 |
| --- | --- |
| Explorer 메뉴 | `HKLM\Software\Classes\AllFilesystemObjects\shell\7Zipper` |
| COM 클래스 | `{9DF247FA-3897-4193-8F57-58062056862D}` |
| COM 서버 | `SevenZipper.Shell.dll`, x64, Apartment |
| 다중 선택 | `MultiSelectModel=Player` |
| 선택형 연결 프로그램 | `.7z`, `.zip`의 `OpenWithProgids\7Zipper.Archive` 값 |
| Inno Setup AppId | `{8E2C4C73-1712-46F1-956D-B919014935AD}` |

Windows 11 첫 화면의 현대식 메뉴는 이 버전에 등록하지 않음. 지원할 때는 [Microsoft의 데스크톱 확장 등록 문서](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions)에 따라 패키지 ID와 서명된 매니페스트를 준비할 것.

## 02 / 끌어서 내보내기 - 임시 폴더와 복사

1. 앱에서 선택 항목을 `%LOCALAPPDATA%\7Zipper\Drag\<32자리 GUID>`로 미리 풀 것.
2. 마우스 버튼을 누른 상태로 끌기 시작. 폴더는 빈 폴더도 포함하며 효과는 복사만 허용.
3. 완료 또는 취소로 확인되면 마지막 COM·데이터 참조 해제 후 임시 폴더 정리. 비동기 복사는 `EndOperation`도 확인. 완료 여부를 알 수 없으면 보존하며, 남은 임시 폴더는 재부팅 후 새 드래그 작업이 없는 상태에서 직접 삭제할 것.

앱은 `sz_drag_active()`가 1인 동안 STA 메시지 처리를 유지하고 종료를 보류할 것. `sz_drag_files` 호출 후에는 임시 폴더를 앱이 따로 삭제하지 말 것. 네이티브 정리는 처음 연 폴더의 ID를 검증하며, 열린 부모 핸들을 기준으로만 하위 항목에 접근함. 재분석 지점은 따라가지 않음.

## 03 / 빌드와 설치 - x64

x64 MSVC 개발자 셸에서 실행:

```powershell
cmake -S src/Shell -B artifacts/shell-build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=ON
cmake --build artifacts/shell-build
ctest --test-dir artifacts/shell-build --output-on-failure
```

배포본에 `7Zipper.exe`, 네이티브 엔진, 셸 DLL, 런타임 및 고지 문서를 포함할 것. 파일을 `artifacts/publish`에 모은 뒤 Inno Setup Compiler로 빌드할 것.

```powershell
ISCC.exe installer/7Zipper.iss
```

- 결과: `dist\7Zipper-Setup-0.1.0-x64.exe`.
- 기본 경로: `{autopf}\7Zipper` (보통 `C:\Program Files\7Zipper`). 설치 경로 화면에서 폴더와 드라이브 선택 가능. 업데이트는 기존 설치 경로를 기본 표시.
- Explorer 메뉴 추가 여부를 선택할 수 있음. 연결 프로그램 옵션은 기본 해제. 선택해도 Windows 기본 앱은 변경하지 않음.
- 설치 관리 시험: `/CURRENTUSER`로 관리자 권한 없이 설치하면 등록 위치가 HKCU로 바뀜. 일반 실행의 기본은 관리자 권한이 필요한 모든 사용자 설치.
- Explorer가 업데이트 중 DLL을 사용하는 경우 Windows 재시작 후 교체 또는 삭제될 수 있음. 옵션을 해제한 업데이트와 제거는 해당 옵션의 등록값만 정리.
- `/CURRENTUSER` 설치는 사용 중인 DLL을 시스템 재시작 때 교체할 권한이 없음. 탐색기가 DLL을 사용 중이면 로그아웃 후 다시 로그인해 설치 또는 제거할 것.

## 04 / 검증 - 자동 검사와 실기 확인

| 구분 | 확인 항목 |
| --- | --- |
| 자동: `shell_wire` | 셸 COM 생성과 하위 명령 열거. 한글·이모지를 포함한 650개 경로를 파이프에서 확인. 전체 경로가 명령줄 제한을 넘는 경우와 불필요한 핸들 미상속 확인. |
| 자동: `drag_lifetime` | Unicode·빈 폴더의 드롭 데이터, 비동기 복사 완료, 데이터 참조 유지, 취소 정리, 결과 불명 시 보존. |
| Windows 클래식 메뉴 API | 임시 사용자 등록으로 .7z에 5개 항목 활성화, 폴더에 압축 3개 항목 표시 확인. 검사 후 임시 등록 제거. |
| 빌드 | MSVC x64 Release DLL과 Inno Setup 7.1.1 설치 스크립트 컴파일. |
| 실제 배포본: 사용자 범위 | 공백을 포함한 사용자 지정 경로 설치, 메뉴와 연결 프로그램 옵션 변경 업데이트, 기본 앱 유지 및 제거 확인. `tests/shell/installer-smoke.ps1 -Installer dist/7Zipper-Setup-0.1.0-x64.exe` |
| 실기: 관리자 권한 필요 | 프로그램 파일 기본 경로, 다른 드라이브 설치 및 모든 사용자 등록·제거. |
| 실기: 일반 사용자 | 압축 파일 안에서 Explorer로 실제 드롭, 대용량 복사 중 창 닫기, Esc와 대상 거절 시 정리. |

참조: [COM 다중 선택](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-employ-the-verb-selection-model), [하위 명령](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iexplorercommand-enumsubcommands), [Shell 비동기 데이터 전송](https://learn.microsoft.com/en-us/windows/win32/shell/datascenarios#dragging-and-dropping-shell-objects-asynchronously).
