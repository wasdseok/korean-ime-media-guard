# Korean IME Media Guard

한글 입력 중 **불필요한 ‘다음 곡’ 명령이 끼어들어 자모가 분리되는 특정 현상**을 막는 Windows 보정 도구입니다. T(ㅅ)·O(ㅐ)에서 관측한 신호를 기준으로 만들었으며, v1.1.0은 기존 T/O/D/한영 규칙에 Y(ㅛ) 해제와 Q(ㅂ)·Backspace 누름 직후의 제한된 대응을 추가합니다.

**[설치용 ZIP 내려받기](https://github.com/wasdseok/korean-ime-media-guard/releases/latest)** · [한국어 설치·복원 안내](README-ko.md) · [개인정보 처리 범위](PRIVACY.md)

Windows 11 x64, .NET Framework 4.x 환경용입니다. 기존 T/O 기준 보정은 한 노트북에서 실제 타이핑으로 효과를 확인했습니다. D 확장과 v1.1.0의 새 규칙은 실제 타이핑으로 보정 효과를 아직 검증하지 않았습니다. 모든 한글 입력 오류를 고치는 범용 도구가 아니며, 최초의 불필요한 신호를 생성한 프로그램·드라이버·장치는 아직 특정하지 못했습니다.

## 사용 방법

1. 윈도우 재설치 후 먼저 메모장에서 `소프트웨어 테스트 키보드 발생`을 직접 입력해 봅니다. 정상이면 설치할 필요가 없습니다.
2. 같은 증상이 재현되면 Releases의 ZIP을 내려받아 **모두 압축 해제**합니다.
3. `install.cmd`를 더블클릭합니다. 관리자 권한은 필요하지 않습니다.
4. 현재 사용자 폴더에 설치되고 즉시 실행되며, 다음 로그인에도 자동 실행됩니다. 실제 한글 입력을 다시 확인합니다.

설치에는 반드시 `install.cmd`를 사용하세요. EXE 직접 실행은 설치·자동 시작 등록을 대신하지 않습니다. 개발용 EXE의 인수 없는 실행은 상대적인 진단 경로를 사용하지만, 설치기는 `--status-path`로 `%LOCALAPPDATA%\KoreanInputGuard\status.json`을 명시합니다.

잠시 멈추려면 알림 영역의 **한글 입력 임시 보정** 아이콘에서 일시 중지 또는 종료를 선택합니다. 완전히 제거하려면 `uninstall.cmd`를 실행합니다. 다른 위치의 기존 보정이나 무관한 시작프로그램은 덮어쓰지 않습니다.

## 정확한 차단 범위

다음 시간 조건 중 하나를 만족하는 입력을 차단 후보로 삼습니다.

- T(ㅅ), O(ㅐ), D(ㅇ), Y(ㅛ) 또는 한/영 키를 뗀 뒤 150ms 이내
- Q(ㅂ) 또는 Backspace를 처음 누른 뒤 30ms 이내이며 아직 떼지 않은 동안. 키를 떼면 종료하며, 길게 누르는 자동 반복으로 시간을 연장하지 않습니다.

후보 입력도 아래 조건을 모두 만족할 때만 차단합니다.

- Windows 가상 키 `VK_MEDIA_NEXT_TRACK` (`0xB0`)
- 스캔 코드 `0`
- Windows의 `LLKHF_INJECTED` 표시가 있는 입력

원래 문자 키, Backspace, 한/영 키와 나머지 키는 전달합니다. 차단한 미디어 키 쌍의 반복·해제도 함께 처리합니다. 정상 프로그램이 같은 방식의 미디어 명령을 이 짧은 시간 안에 보내면 함께 차단될 수 있습니다.

Y 해제 직후의 다음 곡 이벤트는 서로 다른 두 브라우저 진단 기록에서 관측됐습니다. Q와 Backspace 누름 직후는 각각 한 번 관측돼, 해제 기준 150ms 규칙보다 좁은 누름 기준 30ms로 대응합니다. 이 새 사례들의 Windows 주입 표시·스캔 코드는 아직 확인하지 못했으므로, 브라우저에서 관측한 모든 이벤트가 위 차단 조건에 해당한다고 볼 수는 없습니다. K 누름과 겹친 한 사례는 앞선 T 해제 후 150ms 안에 포함될 수 있어 K를 별도 기준 키로 추가하지 않았습니다. D는 기존의 예방적 확장으로 유지하며, D에서 같은 신호가 발생했다고 확인한 것은 아닙니다.

## 검증과 한계

- 기존 T/O 기준 보정으로 실제 예문 5회에서 자모 분리 0개를 확인했습니다.
- 해당 기존 검사에서 불필요한 미디어 신호 19회 차단을 확인했습니다.
- D(ㅇ) 확장은 실제 타이핑으로 아직 검증하지 않았습니다. 같은 신호가 원인인 경우에만 이 보정이 적용됩니다.
- v1.1.0의 Y/Q/Backspace 규칙은 관측한 시점에 맞춘 정책 변경입니다. 새 버전의 실제 타이핑 효과와 해당 사례의 Windows 입력 표시·스캔 코드는 아직 검증하지 않았습니다.
- 순수 정책 시험 결과는 `policy-tests.json`에서 확인할 수 있습니다. 이 시험은 차단 규칙을 검사하며 실제 키보드 타이핑 검증을 대신하지 않습니다.
- 복원 스크립트는 실제 사용자 설정과 분리된 시험 폴더에서 설치·해제·경로 인용·기존 파일 보존·시작 항목 충돌 보호를 검증했습니다.
- 새 Windows 설치, 다른 기종, 관리자 권한 앱 및 보안 입력 화면 전체에서 검증한 것은 아닙니다.
- 코드 서명은 없습니다. 빌드의 출처와 해시를 확인할 수 있도록 소스와 검증 파일을 제공합니다.

공개본에는 개인 Windows 경로, 원본 진단 문장·키 로그, 장치 식별자, 스크린샷, 레지스트리 덤프를 포함하지 않습니다. 날짜와 장치 모델을 제외한 집계 결과만 제공합니다. 실행 후 생성되는 상태·설치 파일이나 직접 저장한 진단 JSON은 개인정보를 포함할 수 있으므로 공개 저장소에 올리지 마세요.

## English

A narrow Windows workaround for unwanted **injected, scan-code-zero Media Next Track events within 150 ms after T, O, D, Y or Hangul key release, or within 30 ms after an initial Q or Backspace key press while that key remains held**. The press window ends on release, and auto-repeat does not extend it. The original character and control keys are forwarded unchanged. Events after T and O interrupted Korean IME composition on one tested laptop, where an earlier T/O guard was validated by physical typing.

Version 1.1.0 adds Y release and short Q/Backspace press windows. Y-associated Media Next events appeared in two separate browser diagnostic recordings; Q and Backspace each appeared once. Those browser records do not establish Windows injection flags or scan codes. The new rules still require the injected, scan-code-zero signature, and their effect on physical typing has not yet been validated. The earlier D extension also remains unverified by a captured D-related fault or physical typing. A K-associated case overlapped the existing T-release window, so K is not a separate trigger. The utility does not identify or repair the underlying source and is not a universal Korean keyboard fix.

Download the release ZIP, extract all files and run `install.cmd` as your normal Windows user. The installer copies the utility into `%LOCALAPPDATA%\KoreanInputGuard`, explicitly sets its local status-file path and registers a per-user startup shortcut. Run `uninstall.cmd` to remove this installation. Test without the utility first after a fresh Windows installation.

The keyboard hook inspects events transiently. It does not save typed sentences or clipboard contents and makes no network requests. Status schema version 3 keeps a bounded local log of at most 256 Media Next events, timing relative to the trigger keys including Y release and Q/Backspace press, counters, PID and update time. The optional offline diagnostic HTML records only its test input and page events in memory; exporting diagnostic JSON is a user action. See [PRIVACY.md](PRIVACY.md).

## 소스 및 라이선스

전체 C# 소스와 Windows PowerShell 설치 소스를 포함합니다. 빌드 명령은 [복원 안내](README-ko.md)에 있습니다. 빌드 위치·도구 차이로 바이너리 해시가 달라질 수 있으며, 수정 배포 시 설치기 기준 해시와 배포 해시를 함께 갱신해야 합니다.

MIT License. 독립적인 개인 프로젝트이며 Microsoft, ASUS 또는 OpenAI의 공식 제품이나 지원 도구가 아닙니다.

## 진단 페이지 보관 한도 (패키지 v1.1.1)

진단 페이지는 최근 이벤트 3,000개를 보관하고 JSON으로 저장합니다. 3,000개를 넘으면 오래된 이벤트부터 제외됩니다. 화면의 최근 이벤트 표는 30개를 표시합니다. 보정 실행 파일과 차단 규칙은 v1.1.0과 같습니다.
