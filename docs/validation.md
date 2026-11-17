# 7Zipper 0.1.0 검증 기록

검증일: 2026-11-17. 아래 결과는 실제 실행 기준. 미검증 항목은 마지막 절에 별도 표시.

## 01 / 환경 - Windows x64

- Windows 10 Pro 22H2, 빌드 19045.
- Intel Core i9-12900H, 논리 프로세서 20개, RAM 약 64GB.
- MSVC 19.44, .NET SDK 10.0.404, .NET 런타임 10.0.15, Inno Setup 7.1.1.
- 별도 호환성 검사 도구: 공식 7-Zip 26.06의 `7za.exe`.

## 02 / 기능 - 생성·해제·원본 보존

| 시험 | 결과 |
|---|---|
| 관리 코드·작업 프로세스 통합 | 107개 assertion 통과. 7z·ZIP·Zstandard ZIP, 암호·분할 조합의 SHA-256 일치 |
| 7z 암호 | AES-256, 파일명 암호화, 암호 누락·오류 처리 통과 |
| ZIP 암호 | AES-256 생성·해제, 외부 ZipCrypto 해제, 인증값만 손상된 AES 파일 거절 통과 |
| 외부 호환성 | 생성 파일의 7-Zip 검사·해제, 외부 LZMA·PPMd 7z 읽기 통과 |
| 파일·폴더 | 한글·이모지, 빈 폴더, 선택 해제, 폴더별 압축, 목록·검색 통과 |
| 알아서풀기 | 단일 파일·최상위 폴더·여러 항목 규칙, 같은 이름의 병렬 작업 폴더 분리 통과 |
| 안전한 경로 | 경로 이탈·장치명·ADS·재분석 지점 거절, 기존 파일 충돌 처리 통과 |
| 원본 보호 | 자기 압축파일·분할 조각·동시 해제 중인 다른 원본의 덮어쓰기 거절 통과 |
| 취소 | 시작 전·작업 중 취소, 미완성 출력·작업 폴더 정리 통과 |
| 손상·누락 | CRC·AES 인증 실패, 분할 조각 누락, 빈 폴더 로컬 헤더 손상 거절 통과 |

재현 명령:

```powershell
./scripts/build.ps1 -Test
python tests/native/smoke.py build/native/SevenZipper.Native.dll C:/path/to/7za.exe
```

## 03 / 대용량 - ZIP64와 분할 ZIP

| 실제 시험 데이터 | 결과 |
|---|---|
| 4GiB + 17바이트 파일 → Deflate ZIP64 | 생성·검사·해제, 원본 SHA-256 일치, 7-Zip 검사·해제 통과 |
| 2GiB + 32MiB 파일 → Store 분할 ZIP, 볼륨 2GiB + 1MiB | 볼륨 크기 준수, 해제 SHA-256 일치, 7-Zip 검사·해제 통과 |
| 빈 파일 900개 → 64KiB 분할 ZIP | 중앙 디렉터리의 다중 볼륨 저장·읽기, 볼륨 크기, 7-Zip 검사 통과 |
| 디렉터리 65,536개 → ZIP64 분할 | 192개 볼륨, 중앙 디렉터리 시작 96번·최종 191번. 네이티브 목록·검사와 7-Zip 검사 통과 |

추가로 ZIP64 필드를 강제로 사용한 소형 합성 입력을 네이티브 회귀 시험에 포함. 이 합성 입력은 7-Zip에서 열리지 않아 독립 호환성 근거에서 제외. 위의 실제 ZIP64 파일은 독립 검사 통과.

대용량 재현 명령. 임시 공간 최대 약 15GiB 필요:

```powershell
python tests/native/large.py build/native/SevenZipper.Native.dll C:/path/to/7za.exe
```

## 04 / 처리 속도 - 1스레드와 20스레드

64개 파일, 총 256MiB. 반복 16KiB 데이터와 서로 다른 4KiB 데이터 혼합. 7z·Deflate 레벨 5, Zstandard 레벨 3, 메모리 예산 8GiB. 각 조건 3회 실행의 시간 중앙값. 모든 해제 결과 SHA-256 확인.

| 형식 | 스레드 | 압축 시간 | 해제 시간 | 압축 시 최대 작업 집합 | 해제 시 최대 작업 집합 |
|---|---:|---:|---:|---:|---:|
| 7z / LZMA2 | 1 | 17.924초 | 3.654초 | 203MiB | 36MiB |
| 7z / LZMA2 | 20 | 1.450초 | 1.072초 | 989MiB | 327MiB |
| ZIP / Deflate | 1 | 2.353초 | 0.435초 | 20MiB | 20MiB |
| ZIP / Deflate | 20 | 0.404초 | 0.064초 | 31MiB | 27MiB |
| ZIP / Zstandard | 1 | 0.537초 | 0.304초 | 29MiB | 29MiB |
| ZIP / Zstandard | 20 | 0.186초 | 0.067초 | 86MiB | 61MiB |

이 데이터에서 압축 시간 단축: LZMA2 약 12.4배, Deflate 약 5.8배, Zstandard 약 2.9배. 다른 프로그램과 비교한 수치가 아니며, 단일 파일·솔리드 스트림·저속 디스크에서는 결과가 달라짐.

네이티브 DLL 직접 측정으로 UI·프로세스 시작 시간 제외. 파일 캐시 강제 초기화 없음. 메모리는 Python 호스트 전체의 작업 집합을 10ms 간격으로 표본화한 최대값이며, 앞선 실행의 메모리 잔류 가능. 전체 프로세스 CPU 시간, 3회 원자료는 [benchmark.json](benchmark.json) 참조. 처리 코덱이 동일한 최종 ZIP 분할 디렉터리 수정 직전 빌드에서 측정.

```powershell
python tests/benchmark.py build/native/SevenZipper.Native.dll benchmark.json 256
```

## 05 / 화면·탐색기·설치

| 범위 | 확인 결과 |
|---|---|
| WPF 화면 | 실제 WPF 렌더링으로 차콜 테마, 250% 배율, 최소 520×400 창 확인. 압축·해제 대화상자, 목록·검색, Zstandard 생성·선택 해제 실행 |
| 메뉴 전달 | `shell_wire` 통과. 한글·이모지 포함 650개 경로, 명령줄 한도를 넘는 다중 선택, 불필요한 핸들 미상속 확인 |
| 클래식 메뉴 | Windows API로 파일의 5개 메뉴·폴더의 3개 메뉴 활성 상태 확인. Explorer가 가상 폴더로 취급하는 ZIP도 해제 메뉴 활성화, 실제 `.7z` 이름 폴더는 해제 메뉴 숨김 확인 |
| 드래그 | `drag_lifetime` 통과. Unicode·빈 폴더, COM 참조·비동기 복사·취소·결과 불명 시 보존 확인. 실제 원본에서 드래그 준비 파일 생성 확인 |
| 설치·업데이트·제거 | `/CURRENTUSER`로 공백 포함 사용자 지정 경로 설치, 메뉴·연결 옵션 변경 업데이트, 기본 앱 유지, 제거 후 등록 정리 통과 |

```powershell
ctest --test-dir build/shell --output-on-failure
./tests/shell/installer-smoke.ps1 -Installer dist/7Zipper-Setup-0.1.0-x64.exe
```

## 06 / 미검증·배포 범위

- Windows 11 실기 및 새 컨텍스트 메뉴: 미검증·미제공. 첫 릴리스는 클래식 메뉴 사용.
- 관리자 권한으로 Program Files에 설치·제거, 실제 다른 드라이브 설치: 미검증. 기본 경로 변수·경로 선택 화면은 구현·빌드 확인.
- 실제 Explorer로 마우스 드롭, 대용량 비동기 복사 도중 창 닫기: 수동 시험 미완료. 준비 파일·OLE 수명 자동 시험만 완료.
- 화면 캡처 API 시간 초과로 WPF RenderTargetBitmap 사용. OS 전체 화면 캡처와 모든 배율의 실기 검증은 미완료.
- 코드 서명·SmartScreen 평판 없음. ARM64·RAR·EGG·ALZ·기존 압축파일 편집·자동 업데이트 미제공.
- 제한 없는 악성 압축파일 퍼징이나 외부 보안 감사는 수행하지 않음. 자동 시험·독립 형식 검사 결과로만 판단할 것.
