# 7Zipper v0.1.6 검증 기록

검증일: 2026-11-25. Windows x64, .NET SDK 10.0.404 및 GitHub Actions Windows 2025 CI.

## 01 / ZIP → 7z 변환 엔진 및 다중 변환

- **개별 변환 및 기본 옵션:** `ArchiveService.ConvertZipTo7zAsync`와 `ConversionOptions` 기본값(`Level = 9`, 최대 압축률) 검증.
- **구조·메타데이터 보존:** 임시 스테이징 폴더의 루트 하위 항목만 열거하여 원본 ZIP과 동일한 내부 경로 구조·빈 폴더·수정 시각 보존, `Zone.Identifier` 전파.
- **다중 병렬 변환:** 여러 ZIP 파일 선택 시 각 ZIP을 별도의 `.7z`로 변환하고 `BatchProgress`로 전체·현재 진행률 및 완료 개수 표시.
- **원본 보호 및 충돌 회피:** 기존 `.7z` 존재 시 `이름 (2).7z`로 안전 저장, 원본 ZIP 기본 유지, `DeleteSourceAfterSuccess = true` 선택 시 `.7z` 게시 완료 후에만 원본 삭제.

## 02 / UI 및 탐색기 메뉴 검증

| 항목 | 확인 내용 |
| --- | --- |
| `ConvertDialog` | 최대 압축(`Level 9`) 기본 선택, ZIP 전용 필터링, 다중 선택 안내 문구, `400×270`·`512×344`·`586×730` 스크롤 및 성능 설정 배치 통과 |
| 메인 툴바 | `520×400` 및 `1060×690` 렌더링에서 5개 툴바 버튼이 단일 행에 정렬되고 빈 상태 안내와 진행률 영역이 겹치지 않음 확인 |
| 탐색기 메뉴 (`shell_wire`) | 6개 하위 명령 열거, `.zip`·`.z01`에서 `7z로 변환` 활성화, `.7z`·`.7z.002`·폴더에서 숨김 확인 |
| 배포 앱 명령 (`shell_installed`) | 실제 배포본 `7Zipper.exe` + `SevenZipper.Shell.dll`로 `convert-7z` 호출 후 생성된 `.7z` 시그니처와 내용 일치 검증 |

재현 명령:

```powershell
pwsh ./scripts/build.ps1 -Test -Publish -Installer
```

## 03 / 검증 범위

- 코드 서명 미적용. 기본 설치 경로·경로 선택·기존 셸 등록 방식 유지.
- 전체 네이티브 엔진·다중 ZIP→7z 통합 테스트·셸 명령·FAT32·인스톨러 검증은 GitHub Actions Windows CI에서 실행.
