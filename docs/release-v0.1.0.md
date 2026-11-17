# 7Zipper v0.1.0

공식 LZMA SDK와 Zstandard를 사용하는 Windows x64 압축 프로그램의 첫 공개 릴리스.

- 기본 7z/LZMA2, ZIP/Deflate·Store·Zstandard, ZIP64.
- 다중 코어 압축·해제, 폴더별 압축, 분할 압축, 알아서풀기.
- 7z·ZIP AES-256 암호, 7z 파일명 암호화.
- 한국어 차콜 테마, 목록·검색·선택 해제·무결성 검사, 양방향 드래그.
- 탐색기 클래식 메뉴. Windows 11에서는 **더 많은 옵션 표시**에서 사용.
- 기본 Program Files 설치, 설치 위치 변경, .NET 런타임 포함.

- **설치판:** `7Zipper-Setup-0.1.0-x64.exe`
- **포터블:** `7Zipper-0.1.0-win-x64-portable.zip`
- **무결성 확인:** `SHA256SUMS.txt`

7z 분할은 `.7z.001`, ZIP 분할은 마지막 `.zip`으로 열 것. 모든 조각을 같은 폴더에 보관할 것. Zstandard ZIP은 압축 방식 93을 지원하는 프로그램 필요.

107개 관리 코드·프로세스 통합 assertion, 셸 자동 시험, 네이티브 회귀 시험, 공식 7-Zip 교차 검사 완료. 4GiB 초과 ZIP64와 2GiB 초과 분할 ZIP의 원본 SHA-256 일치 확인. 사용자 지정 경로의 사용자 범위 설치·옵션 변경 업데이트·제거 확인.

검증 범위·실측 속도·미검증 항목: [검증 기록](https://github.com/yunhyok/7zipper/blob/v0.1.0/docs/validation.md).

첫 릴리스는 코드 서명 미적용. Windows 11 실기, 관리자 권한 Program Files 설치, 실제 Explorer 마우스 드롭은 수동 시험 미완료. 비정상 종료 후 남은 드래그 임시 파일은 재부팅 후 `%LOCALAPPDATA%\7Zipper\Drag`에서 수동 정리 가능.

공식 7-Zip 프로젝트와 별개의 MIT 오픈 소스 프로그램.
