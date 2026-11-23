# 7Zipper v0.1.5 검증 기록

검증일: 2026-11-23. Windows x64, .NET SDK 10.0.404 및 GitHub Actions Windows 2025 CI.

## 01 / 오류 수정 검증

- **드래그 준비 마우스 캡처:** `OnRowMouseUp`에서 `_deferredClick` 여부와 무관하게 `EntryGrid` 캡처를 해제하고, 작업 실패 시 모달 오류 표시 전 캡처 해제 보장.
- **분할 7z 탐색기 메뉴:** `src/Shell/commands.cpp`에서 `.7z.<숫자>` 패턴 인식. `tests/shell/shell_wire.cpp`에 `split.7z.002` 활성화 및 `split.7z.txt` 숨김 검증 추가.
- **대소문자 혼용 폴더 트리:** `ArchiveContents`를 `StringComparer.OrdinalIgnoreCase`로 통일. `SevenZipper.AppTests`에서 `Folder`, `folder/readme.txt`, `FOLDER/Sub/data.bin` 병합 탐색·선택·크기 합산 검증.
- **배치 완료 개수 동기화:** `ProgressDisplayChecks`에서 마지막 비동기 진행률 콜백 없이 작업이 완료되어도 `4/4개 완료`가 동기화되는지 확인.
- **파일 시스템 안전성:** `SevenZipper.Tests --managed`에서 `ArchiveStem`의 끝 점(`report..zip`)·예약 장치명(`CON.zip`) 정제, `ArchiveService.OutputExists`의 분할 볼륨(`.7z.001`, `.z01`) 감지, `PathSafety.DeleteOwnedDirectory`의 읽기 전용 하위 폴더 삭제 검증.
- **리소스 정리:** `WorkerHost` 취소 지연 타이머를 작업 종료 시 즉시 취소, `_dragCleanup` 예외 누적 차단, `seven.cpp`의 `OutStream::SetSize` 축소 시 존재 볼륨 확인.

## 02 / UI/UX 개선 검증

| 항목 | 확인 내용 |
| --- | --- |
| 충돌 일괄 적용 | `TaskDialog.AskCollision`의 `이후 모든 같은 이름 항목에 적용` 선택이 단일 작업 범위에서만 유지·초기화됨 확인 |
| 암호 표시 토글 | 새 압축·압축 풀기·암호 입력 창에서 평문 전환 및 창 종료 시 입력 버퍼 초기화 확인 |
| 압축 옵션 | 저장만(무압축) 선택, Zstandard 전환 시 수준 매핑, 파일/폴더 드롭 추가, `Delete` 키 제거, 분할 볼륨/메모리 한도 사전 검증 확인 |
| 메인 화면 | 창 제목 파일명 표시, 검색어 지우기(`×`), 드롭 오버레이, 결과 `폴더 열기`, 크기·압축률 요약, `PrimaryAction`/`ActionToggle` 스타일 확인 |
| 레이아웃 회귀 | `CompressionLayoutChecks`(400×270, 512×344, 586×730 DIP) 및 `ProgressDisplayChecks`(520×400, 1060×690 DIP) 전체 통과 |

로컬 빌드(`Release`, 경고 0·오류 0), 관리 코드 검사 36개 assertion 및 WPF UI/레이아웃 검사 통과. 전체 네이티브 엔진·셸 DLL·실제 배포 앱·FAT32·인스톨러 검증은 GitHub Actions Windows CI에서 실행.

재현 명령:

```powershell
pwsh ./scripts/build.ps1 -Test -Publish -Installer
```

## 03 / 검증 범위

- 코드 서명 미적용. 기본 설치 경로·경로 선택·기존 셸 등록 방식 유지.
- 다중 모니터 DPI 전환 및 Windows 11 실기 수동 드롭은 이전 검증 기록 참조.
