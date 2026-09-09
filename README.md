# Korean IME Media Guard

한글 입력 중 **키를 뗀 직후 불필요한 ‘다음 곡’ 명령이 끼어들어 자모가 분리되는 특정 현상**을 막는 Windows 보정 도구입니다. T(ㅅ)·O(ㅐ)에서 관측한 신호를 기준으로 만들었으며, v1.0.1부터 D(ㅇ)도 차단 판단의 기준 키에 포함합니다.

**[설치용 ZIP 내려받기](https://github.com/wasdseok/korean-ime-media-guard/releases/latest)** · [한국어 설치·복원 안내](README-ko.md) · [개인정보 처리 범위](PRIVACY.md)

Windows 11 x64, .NET Framework 4.x 환경용입니다. 기존 T/O 기준 보정은 한 노트북에서 실제 타이핑으로 효과를 확인했으며, D 확장에 대한 실제 타이핑 검증은 아직 하지 않았습니다. 모든 한글 입력 오류를 고치는 범용 도구가 아니며, 최초의 불필요한 신호를 생성한 프로그램·드라이버·장치는 아직 특정하지 못했습니다.

## 사용 방법

1. 윈도우 재설치 후 먼저 메모장에서 `소프트웨어 테스트 키보드 발생`을 직접 입력해 봅니다. 정상이면 설치할 필요가 없습니다.
2. 같은 증상이 재현되면 Releases의 ZIP을 내려받아 **모두 압축 해제**합니다.
3. `install.cmd`를 더블클릭합니다. 관리자 권한은 필요하지 않습니다.
4. 현재 사용자 폴더에 설치되고 즉시 실행되며, 다음 로그인에도 자동 실행됩니다. 실제 한글 입력을 다시 확인합니다.

설치에는 반드시 `install.cmd`를 사용하세요. EXE 직접 실행은 설치·자동 시작 등록을 대신하지 않습니다. 개발용 EXE의 인수 없는 실행은 상대적인 진단 경로를 사용하지만, 설치기는 `--status-path`로 `%LOCALAPPDATA%\KoreanInputGuard\status.json`을 명시합니다.

잠시 멈추려면 알림 영역의 **한글 입력 임시 보정** 아이콘에서 일시 중지 또는 종료를 선택합니다. 완전히 제거하려면 `uninstall.cmd`를 실행합니다. 다른 위치의 기존 보정이나 무관한 시작프로그램은 덮어쓰지 않습니다.

## 정확한 차단 범위

다음 조건을 모두 만족하는 입력만 차단합니다.

- T(ㅅ), O(ㅐ), D(ㅇ) 또는 한/영 키를 뗀 뒤 150ms 이내
- Windows 가상 키 `VK_MEDIA_NEXT_TRACK` (`0xB0`)
- 스캔 코드 `0`
- Windows의 `LLKHF_INJECTED` 표시가 있는 입력

원래 T/O/D/한영 키와 나머지 키는 전달합니다. D 키 추가는 같은 조건의 불필요한 미디어 신호에 대응하도록 범위를 확장한 것이며, D에서 해당 신호가 실제로 발생했다고 확인한 것은 아닙니다. 차단한 미디어 키 쌍의 반복·해제도 함께 처리합니다. 정상 프로그램이 같은 방식의 미디어 명령을 이 짧은 시간 안에 보내면 함께 차단될 수 있습니다.

## 검증과 한계

- 기존 T/O 기준 보정으로 실제 예문 5회에서 자모 분리 0개를 확인했습니다.
- 해당 기존 검사에서 불필요한 미디어 신호 19회 차단을 확인했습니다.
- D(ㅇ) 확장은 실제 타이핑으로 아직 검증하지 않았습니다. 같은 신호가 원인인 경우에만 이 보정이 적용됩니다.
- 순수 정책 시험 결과는 `policy-tests.json`에서 확인할 수 있습니다. 이 시험은 차단 규칙을 검사하며 실제 키보드 타이핑 검증을 대신하지 않습니다.
- 복원 스크립트는 실제 사용자 설정과 분리된 시험 폴더에서 설치·해제·경로 인용·기존 파일 보존·시작 항목 충돌 보호를 검증했습니다.
- 새 Windows 설치, 다른 기종, 관리자 권한 앱 및 보안 입력 화면 전체에서 검증한 것은 아닙니다.
- 코드 서명은 없습니다. 빌드의 출처와 해시를 확인할 수 있도록 소스와 검증 파일을 제공합니다.

공개본에는 개인 Windows 경로, 원본 진단 문장·키 로그, 장치 식별자, 스크린샷, 레지스트리 덤프를 포함하지 않습니다. 날짜와 장치 모델을 제외한 집계 결과만 제공합니다. 실행 후 생성되는 상태·설치 파일이나 직접 저장한 진단 JSON은 개인정보를 포함할 수 있으므로 공개 저장소에 올리지 마세요.

## English

A narrow Windows workaround for unwanted **injected, scan-code-zero Media Next Track events within 150 ms after T, O, D or Hangul key release**. Events after T and O interrupted Korean IME composition on one tested laptop. Version 1.0.1 adds D as a trigger; this extension has not yet been validated by physical typing or a captured D-related fault. The utility does not identify or repair the underlying source and is not a universal Korean keyboard fix.

Download the release ZIP, extract all files and run `install.cmd` as your normal Windows user. The installer copies the utility into `%LOCALAPPDATA%\KoreanInputGuard`, explicitly sets its local status-file path and registers a per-user startup shortcut. Run `uninstall.cmd` to remove this installation. Test without the utility first after a fresh Windows installation.

The keyboard hook inspects events transiently. It does not save typed sentences or clipboard contents and makes no network requests. A bounded local log stores Media Next events, timing relative to the trigger keys, counters, PID and update time. The optional offline diagnostic HTML records only its test input and page events in memory; exporting diagnostic JSON is a user action. See [PRIVACY.md](PRIVACY.md).

## 소스 및 라이선스

전체 C# 소스와 Windows PowerShell 설치 소스를 포함합니다. 빌드 명령은 [복원 안내](README-ko.md)에 있습니다. 빌드 위치·도구 차이로 바이너리 해시가 달라질 수 있으며, 수정 배포 시 설치기 기준 해시와 배포 해시를 함께 갱신해야 합니다.

MIT License. 독립적인 개인 프로젝트이며 Microsoft, ASUS 또는 OpenAI의 공식 제품이나 지원 도구가 아닙니다.
