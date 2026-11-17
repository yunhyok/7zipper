# 7Zipper v0.1.2 검증 기록

검증일: 2026-11-17. Windows 10 Pro 22H2, x64, 시스템 OEM 코드페이지 949, 화면 배율 250% (240 DPI).

## 01 / ZIP 파일명 - CP437와 CP949

- 이전 배포본에서 DOS ZIP의 `é.txt`가 손상된 이름으로 해석되어 열리지 않는 현상 재현.
- 시스템 OEM 해석의 유효성을 검사하고, 잘못된 바이트만 CP437로 다시 해석하도록 수정.
- `é.txt`, `ä.txt`, `ü0.txt`, CP949 `한글명.txt`의 정확한 목록·해제 파일명과 내용 확인. 최종 자체 포함 실행 파일의 작업 프로세스에서도 통과.
- Unix UTF-8 이름, UTF-8 표시와 유니코드 확장 필드의 기존 처리 유지. 네이티브 회귀 시험 통과.

**한계:** 두 코드페이지에서 모두 유효한 바이트는 기존 OEM 해석 유지. 자동 판별만으로 모든 구형 ZIP의 원래 코드페이지를 확정할 수 없음.

## 02 / 마우스 - 실제 Explorer 드래그

테스트 앱에서 실제 `MainWindow`와 네이티브 DLL 사용. `SwapMouseButton`으로 Windows 설정을 변경하고 `SendInput`으로 물리적 버튼 입력 주입. 실제 Explorer의 파일 목록 영역으로 OLE 드롭. WPF 이벤트나 드롭 결과를 가짜 호출로 대체하지 않음.

| 시험 | 확인 결과 |
| --- | --- |
| 기본 설정 복사 | 물리적 왼쪽 버튼 유지 → WPF 논리적 Left → Explorer 복사 통과 |
| 단추 교체 복사 | `SM_SWAPBUTTON=1`, 물리적 오른쪽 버튼 유지 → WPF 논리적 Left → Explorer 복사 통과 |
| 단추 교체 취소 | 오른쪽 버튼을 누른 채 Esc → 취소 상태, 대상 폴더 비어 있음, 임시 폴더 제거 |
| 파일 구성 | 한글·이모지 파일명, 암시적 중첩 폴더, 명시적 빈 폴더, 선택 항목 3개 보존 |
| 복사 내용 | 8,193바이트 파일과 25바이트 CSV의 SHA-256 일치 |
| 복사 수명 | 복사 중 임시 파일 유지. 마지막 COM·데이터 참조 해제 후 임시 폴더 제거 |
| 환경 복원 | 원래 마우스 단추 설정·커서 위치 복원. 시험용 Explorer 창만 닫음 |

파일 SHA-256:

```text
7e3691790cd64b19d4edb1a80e988214515abeb53aa0f34ffbfe4b4bf405d120  8193-byte file
d8bddb1879d26a80feb6171669399bedaa80bd7607c88a4f35ec09177c43b653  nested CSV
```

Explorer가 `StartOperation` 이후 `EndOperation` 없이 `CFSTR_PERFORMEDDROPEFFECT=COPY`만 보내는 동작 확인. 해당 완료 통지를 반영하도록 수정. 이 통지는 파일 복사 전에 전달되는 `CFSTR_LOGICALPERFORMEDDROPEFFECT`와 구별할 것. [Microsoft 형식 정의](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard#cfstr_performeddropeffect).

Explorer가 복사 후에도 데이터 참조를 보유하는 경우 확인. 교체 버튼 복사 시험에서는 대상 파일 해시를 확인한 뒤 시험용 Explorer 창을 닫아 참조 해제를 확인. 참조가 남으면 임시 폴더 정리와 앱 종료가 지연될 수 있음.

재현: 잠금이 해제된 데스크톱에서 다음 명령 실행. 시험 중 마우스·키보드를 조작하지 말 것.

```powershell
./scripts/build.ps1 -Test
dotnet run --project tests/SevenZipper.DesktopTests -c Release -- --interactive artifacts
```

## 03 / 회귀 검사 - 압축 엔진과 배포

- 로컬 관리 코드·작업 프로세스 통합 검사: 112개 assertion 통과. FAT32 선택 검사는 로컬에서 생략.
- 셸 CTest: 2/2 통과. 완료 통지만 수신, 데이터 참조 유지, 명시적 실패 우선, 잘못된 완료 자료·결과 불명 시 보존 포함.
- 네이티브 검사: 생성·해제, 선택, 암호, MAC·CRC 손상, 분할, 경로 보호, 취소 통과. CP437 수정 엔진의 독립 7-Zip 검사 통과.
- MSVC Release, .NET 자체 포함 포터블, Inno Setup 설치판 빌드 통과.
- 설치판 `/CURRENTUSER` 시험: 공백 포함 사용자 지정 경로 설치, 메뉴·연결 옵션 변경 업데이트, 기본 앱 유지, 제거 후 등록 정리 통과.

## 04 / 미검증 범위

- Windows 11 실기, 사람이 직접 마우스를 조작한 시험, 대용량 비동기 드롭 중 앱 종료는 미검증.
- 관리자 권한의 Program Files 설치·제거와 다른 드라이브 실설치는 미검증. 기본 경로 변수와 경로 선택 화면 유지.
- 변경 없는 4GiB ZIP64·성능 시험은 재실행하지 않음. 기존 결과는 [v0.1.0 검증 기록](validation.md) 참조.
- 코드 서명 미적용. Windows 11의 새 컨텍스트 메뉴는 미제공.
