# SwarmUI 인스턴스 격리 계획서

## 목표

이번 계획의 목표는 아래 네 가지다.

1. 작업자마다 `SwarmUI` 인스턴스를 따로 사용한다.
2. 실제 GPU 실행은 중앙 `ComfyUI` 백엔드가 담당한다.
3. `workflow / input / output` 파일 저장은 지금처럼 backend `ComfyUI` 측 SMB 공유를 유지한다.
4. `대기열 / history / gallery / feed / download`는 각 `SwarmUI` 인스턴스 기준으로만 보이게 만든다.

즉, 파일 저장은 공유하되 화면에 보이는 작업 상태와 결과물 목록은 공유하지 않는다.

---

## 확정 구조

이번 계획의 기준 아키텍처는 아래와 같다.

- 작업자 A: `SwarmUI-A`
- 작업자 B: `SwarmUI-B`
- 작업자 C: `SwarmUI-C`
- 중앙 GPU 서버들: `ComfyUI` backend 여러 대
- 공유 스토리지: SMB로 연결된 `workflow / input / output`

핵심은 아래와 같다.

- 중앙 `SwarmUI` 1대로 모든 사용자를 받지 않는다.
- 각 작업자는 자기 `SwarmUI` 인스턴스를 사용한다.
- 각 `SwarmUI` 인스턴스는 자기 `DB`, 자기 `세션`, 자기 `queue view`, 자기 `history index`, 자기 `gallery index`, 자기 `feed`를 가진다.
- 실제 생성만 중앙 `ComfyUI` GPU backend로 보낸다.

---

## 확정 결론

이 구조는 구현 가능하다.

### 1. backend `ComfyUI` 수정

하지 않는다.

- 중앙 `ComfyUI`는 GPU 실행기 역할만 한다.
- 사용자 계정, 사용자별 gallery, 사용자별 history 개념은 `ComfyUI`가 몰라도 된다.
- 이번 계획은 `SwarmUI` 쪽에서만 구현한다.

### 2. SMB 공유

유지한다.

- `workflow / input / output` 저장을 위해 SMB 공유를 유지한다.
- 공유 폴더를 직접 브라우징하는 방식만 버린다.

### 3. 대기열 분리

구현한다.

- 실제 backend 내부 큐는 공용이어도 된다.
- 사용자 화면에 보이는 대기열은 각 `SwarmUI` 인스턴스가 자기 요청만 보여주게 만든다.

### 4. history / gallery / feed / download 분리

구현한다.

- 공유 output 폴더 전체를 스캔해서 보여주지 않는다.
- 각 `SwarmUI` 인스턴스가 자기 생성 요청과 자기 결과만 별도 기록한다.
- gallery와 download는 그 기록만 기준으로 동작하게 만든다.

---

## 핵심 원칙

이번 계획의 핵심 원칙은 아래 다섯 가지다.

1. 공유 폴더는 저장용이다.
2. 공유 폴더는 UI 목록의 source of truth가 아니다.
3. queue / history / gallery / feed는 인스턴스별 인덱스를 source of truth로 삼는다.
4. download는 인스턴스 인덱스에 등록된 파일만 허용한다.
5. backend `ComfyUI`는 수정하지 않는다.

---

## 왜 이 구조로 가는가

현재 문제는 파일 자체가 공유된다는 점보다, 공유 폴더와 backend 상태를 그대로 사용자 화면에 노출한다는 점이다.

즉 지금 문제는 아래와 같다.

- output 공유 폴더를 스캔하면 다른 작업자 결과물이 섞여 보인다.
- shared alias를 브라우징하면 backend 공용 output이 그대로 노출된다.
- 공용 브라우징 구조를 쓰면 history와 gallery 분리가 깨진다.

반대로 아래처럼 바꾸면 분리가 가능하다.

- 파일은 계속 공유 폴더에 저장
- 생성 요청을 보낸 `SwarmUI` 인스턴스가 자기 요청 ID를 기록
- 결과가 돌아오면 자기 인스턴스 결과 인덱스에만 등록
- history / gallery / feed는 이 인덱스만 표시
- download도 이 인덱스에 등록된 파일만 허용

즉, **저장은 공유하고 표시는 분리한다**가 이번 설계의 핵심이다.

---

## 대상 범위

이번 계획에서 다루는 범위는 아래다.

1. 인스턴스별 작업 대기열
2. 인스턴스별 현재 결과 피드
3. 인스턴스별 이전 생성 히스토리
4. 인스턴스별 갤러리
5. 인스턴스별 다운로드 허용 범위

이번 계획에서 바로 다루지 않는 범위는 아래다.

1. backend `ComfyUI` 코드 수정
2. `ExtraNodes` 수정
3. 모델 관리 기능
4. 커스텀 노드 관리 기능
5. 중앙 사용자 통합 인증
6. 중앙 DB 공유

---

## 데이터 흐름

## 1. 생성 요청

1. 작업자가 자기 `SwarmUI` 인스턴스에서 생성 요청을 보낸다.
2. 해당 `SwarmUI` 인스턴스는 자기 요청 레코드를 만든다.
3. 요청은 중앙 `ComfyUI` backend로 전달된다.
4. 사용자 화면 대기열에는 자기 인스턴스 요청만 표시한다.

## 2. 생성 결과 수신

1. backend가 결과 파일을 공유 output 폴더에 저장한다.
2. 요청을 보낸 `SwarmUI` 인스턴스가 결과 경로와 메타데이터를 받는다.
3. 해당 인스턴스는 자기 결과 인덱스에만 파일을 등록한다.
4. feed와 history는 이 인덱스를 기준으로 즉시 갱신한다.

## 3. 결과물 조회

1. gallery/history는 공유 output 폴더 전체를 재귀 탐색하지 않는다.
2. 각 `SwarmUI` 인스턴스는 자기 결과 인덱스를 조회한다.
3. 화면에는 자기 인스턴스가 만든 파일만 보인다.

## 4. 다운로드

1. 사용자가 gallery에서 파일 다운로드를 누른다.
2. `SwarmUI`는 먼저 해당 파일이 자기 인덱스에 등록된 항목인지 검사한다.
3. 등록된 항목이면 공유 폴더에서 파일을 읽어 프록시 다운로드한다.
4. 등록되지 않은 항목이면 거부한다.

---

## 구현 방식

## 1번 계획: 인스턴스별 대기열 분리

## 목표

사용자 화면에서 보이는 대기열은 자기 `SwarmUI` 인스턴스가 넣은 작업만 보여야 한다.

## 구현 방식

- backend의 공용 큐를 사용자 화면에 그대로 노출하지 않는다.
- 각 `SwarmUI` 인스턴스가 자기 요청 목록을 별도 저장한다.
- 대기중, 실행중, 완료, 실패 상태는 자기 인스턴스 요청 레코드 기준으로 갱신한다.

## 해야 할 일

1. 요청 생성 시 인스턴스 로컬 작업 레코드를 만든다.
2. 작업 레코드에 request id, backend id, 생성 시각, 상태를 저장한다.
3. 기본 상태 표시와 queue view는 이 레코드만 사용하게 바꾼다.
4. 다른 인스턴스 요청은 어떤 화면에도 표시하지 않는다.

## 완료 기준

- 작업자 A는 작업자 B 요청을 볼 수 없다.
- 작업자 B는 작업자 A 요청을 볼 수 없다.
- 화면상 대기열은 인스턴스 로컬 작업만 보여준다.

---

## 2번 계획: 인스턴스별 feed 분리

## 목표

현재 생성 결과 피드는 자기 인스턴스에서 생성된 결과만 보여야 한다.

## 구현 방식

- 현재 요청으로 수신한 결과만 `feed`에 추가한다.
- shared output 폴더를 다시 스캔해서 현재 피드를 만들지 않는다.
- 브라우저 새로고침 후 복원도 인스턴스 로컬 최근 결과 목록만 사용한다.

## 해야 할 일

1. 현재 세션 결과 수신 흐름과 인스턴스 로컬 작업 기록을 연결한다.
2. 피드 복원 시 공유 root가 아니라 인스턴스 최근 작업 목록만 사용한다.
3. 다른 인스턴스 결과가 현재 피드에 들어올 수 있는 공용 진입점을 제거한다.

## 완료 기준

- 작업자 A의 피드에 작업자 B 결과가 보이지 않는다.
- 작업자 B의 피드에 작업자 A 결과가 보이지 않는다.

---

## 3번 계획: 인스턴스별 history / gallery 분리

## 목표

기본 history와 gallery는 자기 인스턴스가 만든 결과만 보여야 한다.

## 구현 방식

- 공유 output 폴더 전체 탐색을 기본 history source로 쓰지 않는다.
- 인스턴스별 결과 인덱스를 저장하고, history와 gallery는 이 인덱스를 조회한다.
- 기본 UI에서 shared alias와 공용 root 브라우징 진입점을 없앤다.

## 해야 할 일

1. 인스턴스별 결과 인덱스 저장 구조를 만든다.
2. 생성 성공 시 결과 파일 경로와 메타데이터를 인덱스에 기록한다.
3. 기본 `ListImages` 기반 shared browse 흐름 대신 인덱스 기반 조회 API를 추가하거나 교체한다.
4. 기존 공용 special folder alias 노출 경로를 기본 사용자 화면에서 제거한다.

## 완료 기준

- 작업자 A gallery/history에 작업자 B 결과가 보이지 않는다.
- 작업자 B gallery/history에 작업자 A 결과가 보이지 않는다.
- 기본 gallery/history는 인스턴스 로컬 인덱스만 조회한다.

---

## 4번 계획: 인스턴스별 download 분리

## 목표

사용자는 자기 gallery에 등록된 결과물만 다운로드할 수 있어야 한다.

## 구현 방식

- 다운로드는 상대 경로만 보고 허용하지 않는다.
- 인스턴스 로컬 결과 인덱스에 등록된 파일인지 먼저 검사한다.
- 허용된 파일만 공유 output 폴더에서 읽어 전달한다.

## 해야 할 일

1. 인덱스 기반 파일 조회 API를 만든다.
2. 파일 ID 또는 인덱스 키로 다운로드를 요청하게 바꾼다.
3. 인덱스에 없는 파일은 다운로드를 거부한다.
4. 메타데이터 다운로드도 같은 기준으로 묶는다.

## 완료 기준

- 작업자 A는 작업자 B 파일을 다운로드할 수 없다.
- gallery에 없는 파일은 직접 경로를 알아도 다운로드할 수 없다.

---

## 저장 설계

## 인스턴스 로컬 저장소

각 `SwarmUI` 인스턴스는 아래 데이터를 자기 쪽에 저장한다.

- 작업 레코드
- 결과 인덱스
- 결과 메타데이터 요약
- 최근 feed 목록

## 공유 저장소

SMB 공유 폴더는 아래 용도로만 사용한다.

- workflow 원본 파일 저장
- input 파일 저장
- output 파일 저장

즉, 공유 저장소는 파일 저장소이고, 사용자 화면용 목록 저장소는 아니다.

---

## Release 1 범위

Release 1은 아래만 구현한다.

1. 인스턴스 로컬 작업 레코드 생성
2. 인스턴스 로컬 결과 인덱스 생성
3. 기본 feed를 인스턴스 로컬 결과 기준으로 전환
4. 기본 history/gallery를 인덱스 기준으로 전환
5. 다운로드를 인덱스 등록 파일만 허용하도록 전환
6. shared alias와 공용 root 직접 브라우징 제거

---

## 코드 수정 방향

이번 계획에서 주요 수정 대상은 아래다.

### [T2IAPI.cs](/Users/ahnhs2k/Desktop/personal/SwarmUI/src/WebAPI/T2IAPI.cs)

- 생성 요청과 결과 수신 시 인스턴스 로컬 작업/결과 기록을 연결한다.
- 기존 history browse 흐름을 인덱스 기반 조회로 분리한다.

### [Session.cs](/Users/ahnhs2k/Desktop/personal/SwarmUI/src/Accounts/Session.cs)

- 생성 완료 시점의 파일 경로, 메타데이터, request id를 인덱스 레코드와 연결한다.

### [BasicAPIFeatures.cs](/Users/ahnhs2k/Desktop/personal/SwarmUI/src/WebAPI/BasicAPIFeatures.cs)

- 화면상 대기열 표시를 인스턴스 로컬 작업 레코드 기준으로 정리한다.

### [outputhistory.js](/Users/ahnhs2k/Desktop/personal/SwarmUI/src/wwwroot/js/genpage/gentab/outputhistory.js)

- 기존 shared browse UI 대신 인덱스 기반 history/gallery API를 사용하게 바꾼다.

### [currentimagehandler.js](/Users/ahnhs2k/Desktop/personal/SwarmUI/src/wwwroot/js/genpage/gentab/currentimagehandler.js)

- 현재 피드 복원과 gallery 연결을 인스턴스 로컬 결과 목록 기준으로 맞춘다.

### 새 저장 클래스 또는 helper

- 작업 레코드와 결과 인덱스를 저장하는 helper 또는 storage 클래스를 추가한다.
- 이 클래스는 경로 검증, 메타데이터 저장, 다운로드 허용 검사까지 담당한다.

---

## 구현 원칙

1. backend `ComfyUI`는 수정하지 않는다.
2. `ExtraNodes`는 수정하지 않는다.
3. SMB 공유는 유지한다.
4. 공유 폴더 직접 브라우징을 기본 UI source로 쓰지 않는다.
5. 사용자 화면은 인스턴스 로컬 인덱스만 본다.

---

## 제외 범위

이번 단계에서 하지 않는 일은 아래와 같다.

1. 중앙 사용자 계정 통합
2. 중앙 대기열 물리 분리
3. backend 큐 자체 사용자 분리
4. 모델 다운로드 기능 재설계
5. 커스텀 노드 배포 자동화
6. workflow 편집 권한 재설계

---

## 검증 계획

## 1차 검증

1. `SwarmUI-A`에서 생성 요청
2. `SwarmUI-B`에서 생성 요청
3. A 화면 대기열에 B 작업이 보이지 않는지 확인
4. B 화면 대기열에 A 작업이 보이지 않는지 확인

## 2차 검증

1. A가 만든 결과가 A feed/history/gallery에 보이는지 확인
2. B가 만든 결과가 B feed/history/gallery에 보이는지 확인
3. A 화면에 B 결과가 보이지 않는지 확인
4. B 화면에 A 결과가 보이지 않는지 확인

## 3차 검증

1. A gallery에서 A 파일 다운로드 가능 여부 확인
2. B gallery에서 B 파일 다운로드 가능 여부 확인
3. A가 B 파일 경로를 알아도 다운로드 거부되는지 확인
4. B가 A 파일 경로를 알아도 다운로드 거부되는지 확인

---

## 코드 스타일 가이드

## 공통 원칙

- 독스트링과 주석은 **한글로 작성**한다.
- public API, 저장 구조, 다운로드 검증, 사용자 격리 로직에는 반드시 문서화를 붙인다.
- 주석은 "왜 공유 폴더를 직접 브라우징하지 않는가"를 설명해야 한다.

## C#

```csharp
/// <summary>현재 인스턴스가 생성한 결과물만 반환한다.</summary>
/// <param name="session">현재 사용자 세션이다.</param>
/// <param name="limit">반환할 최대 결과 수다.</param>
/// <returns>인스턴스 로컬 결과 인덱스 목록이다.</returns>
public static async Task<JObject> ListInstanceOutputs(Session session, int limit)
{
    ...
}
```

## Python

```python
def validate_download_entry(entry_id: str, instance_id: str) -> bool:
    """다운로드 대상이 현재 인스턴스에 속한 항목인지 검사한다.

    Args:
        entry_id: 결과 인덱스 항목 ID다.
        instance_id: 현재 SwarmUI 인스턴스 ID다.

    Returns:
        현재 인스턴스 소유 항목이면 True를 반환한다.
    """
```

## JavaScript

```javascript
/**
 * 현재 인스턴스 결과 인덱스만 기준으로 gallery를 렌더링한다.
 * @param {Array} items 현재 인스턴스 결과 항목 목록이다.
 * @returns {void}
 */
function renderInstanceOnlyGallery(items) {
    ...
}
```

---

## 최종 결론

이번 계획은 아래 전제로 진행한다.

1. 작업자별 `SwarmUI` 인스턴스 사용
2. 중앙 `ComfyUI` backend는 GPU 실행만 담당
3. SMB 공유 유지
4. 대기열과 `history / gallery / feed / download`는 인스턴스 로컬 인덱스로 분리

즉, 이번 계획은 **backend는 그대로 두고, `SwarmUI`를 인스턴스 단위 작업 공간으로 바꾸는 계획**이다.
