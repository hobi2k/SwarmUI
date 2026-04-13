# 대기열 분리 · 갤러리 아키텍처 설명

이 문서는 아래 두 가지 동작 원리를 코드 수준에서 설명한다.

1. 하나의 ComfyUI 백엔드를 공유하면서도 사용자별로 자기 작업만 대기열에 보이는 이유
2. 갤러리가 SwarmUI를 삭제하지 않는 한 영구 유지되는 이유
3. SwarmUI를 껐다 켤 때마다 대기열 패널이 초기화되는 이유

---

## 1. 전체 구조 개요

```
브라우저 (사용자 A)          브라우저 (사용자 B)
     │                            │
     ▼                            ▼
SwarmUI 서버 (ASP.NET)  ←──────────┘
     │  /ComfyBackendDirect/...
     │  prompt_id 소유권 필터링
     ▼
ComfyUI 백엔드 (공유, 단일 인스턴스)
```

SwarmUI는 ComfyUI와 독립된 서버다.  
사용자의 브라우저는 ComfyUI에 직접 접속하지 않고, 반드시 SwarmUI를 통해 프록시된다.  
이 프록시 레이어에서 사용자별 필터링이 이루어진다.

---

## 2. prompt_id 소유권 추적

### 핵심 파일

- `src/BuiltinExtensions/ComfyUIBackend/ComfyUser.cs`
- `src/BuiltinExtensions/ComfyUIBackend/ComfyUIRedirectHelper.cs`

### 동작

ComfyUI에 작업을 제출할 때 SwarmUI가 `prompt_id`를 생성한다.  
이 `prompt_id`는 `ComfyUser.OwnedPromptIds`에 즉시 등록된다.

```csharp
// ComfyUser.cs
public ConcurrentDictionary<string, byte> OwnedPromptIds = new();

public void RegisterOwnedPromptId(string promptId)
{
    OwnedPromptIds.TryAdd(promptId, 0);
}
```

작업 제출 응답에서 ComfyUI가 내부적으로 재매핑한 ID가 있으면 그 ID도 함께 등록한다.

```csharp
// ComfyUIAPIAbstractBackend.cs (제출 응답 처리 부분)
PromptIdMap.TryAdd($"{promptId}", recvId);
RegisterOwnedPromptId($"{promptId}");
RegisterOwnedPromptId(recvId);
```

`ComfyUser`는 SwarmUI 세션과 1:1 또는 1:N으로 연결된다.  
`ComfyUIRedirectHelper.GetOwnedPromptIds(swarmUser)`를 호출하면 현재 사용자에게 귀속된 모든 `prompt_id`를 수집할 수 있다.

---

## 3. 대기열·이력 응답 필터링

### 핵심 파일

- `src/BuiltinExtensions/ComfyUIBackend/ComfyUIRedirectHelper.cs`

### GET 요청 (조회)

사용자가 ComfyUI 대기열 패널을 열면 브라우저는 다음 경로로 GET 요청을 보낸다.

```
GET /ComfyBackendDirect/queue
GET /ComfyBackendDirect/history
```

SwarmUI는 ComfyUI의 실제 응답을 가로채서 현재 사용자 소유 `prompt_id`만 남기고 나머지를 제거한다.

```csharp
// FilterQueueResponse
if (result["queue_running"] is JArray running)
    result["queue_running"] = new JArray(running.Where(keepItem));
if (result["queue_pending"] is JArray pending)
    result["queue_pending"] = new JArray(pending.Where(keepItem));

// FilterHistoryResponse
foreach (JProperty property in history.Properties())
    if (IsOwnedPromptId(swarmUser, property.Name))
        result[property.Name] = property.Value;
```

결과적으로 사용자 A의 브라우저에는 A의 작업만, 사용자 B의 브라우저에는 B의 작업만 표시된다.  
ComfyUI 백엔드 자체는 모든 작업을 하나의 큐로 처리하지만, 각 사용자는 자신의 슬라이스만 본다.

### WebSocket 이벤트 필터링

ComfyUI는 진행 상황(진행률, 완료 알림)을 WebSocket 메시지로 전달한다.  
SwarmUI는 이 메시지를 사용자에게 전달하기 전에 `prompt_id` 기준으로 체크한다.

```csharp
public static bool ShouldForwardComfyEvent(User swarmUser, JObject parsed)
{
    if (parsed?["data"] is not JObject dataObj
        || !dataObj.TryGetValue("prompt_id", out JToken promptIdTok))
    {
        return true; // prompt_id가 없는 이벤트는 모두 통과
    }
    return IsOwnedPromptId(swarmUser, promptIdTok.ToString());
}
```

`prompt_id`가 없는 이벤트(연결 확인 등)는 그대로 통과되고,  
`prompt_id`가 있는 이벤트는 현재 사용자 소유일 때만 전달된다.

### POST 요청 (삭제·clear)

사용자가 대기열 항목을 삭제하거나 전체 clear 요청을 보내면, SwarmUI는 요청 본문도 필터링한다.

```csharp
public static JObject FilterQueueMutationRequest(User swarmUser, JObject requestBody)
{
    HashSet<string> ownedPromptIds = GetOwnedPromptIds(swarmUser);

    // 특정 항목 삭제: 소유한 것만 허용
    if (requestBody["delete"] is JArray deleteArr)
    {
        filtered["delete"] = new JArray(deleteArr.Where(id => ownedPromptIds.Contains(id?.ToString())));
    }

    // 전체 clear: 소유한 prompt_id를 delete 목록으로 변환
    if (requestBody["clear"]?.Value<bool>() == true && ownedPromptIds.Count > 0)
    {
        filtered["delete"] = new JArray(ownedPromptIds);
    }
}
```

이로써 사용자는 자기 작업만 삭제할 수 있고, 다른 사용자의 작업을 실수로 지우는 일이 생기지 않는다.

---

## 4. 시작 시 대기열 자동 초기화

### 핵심 파일

- `src/BuiltinExtensions/ComfyUIBackend/ComfyUIAPIAbstractBackend.cs`

### 동작

`OwnedPromptIds`는 인메모리 데이터다.  
SwarmUI를 재시작하면 이전 세션의 소유권 정보가 사라진다.  
이 상태에서 ComfyUI에 이전 작업이 남아있으면 다음 문제가 생긴다.

- 대기열 패널에 이전 세션 작업이 그대로 남아 보임
- `OwnedPromptIds`가 비어있어 삭제 요청이 차단됨 (필터가 빈 목록과 대조하므로 아무것도 통과 못 함)

이를 해결하기 위해 백엔드가 처음 `RUNNING` 상태가 될 때 ComfyUI의 history와 queue를 한 번 초기화한다.

```csharp
if (!HasDoneStartupClear)
{
    HasDoneStartupClear = true;
    await HttpClient.PostAsync($"{APIAddress}/history",
        Utilities.JSONContent(new JObject() { ["clear"] = true }), ...);
    await HttpClient.PostAsync($"{APIAddress}/queue",
        Utilities.JSONContent(new JObject() { ["clear"] = true }), ...);
}
```

`HasDoneStartupClear`는 인스턴스 필드이므로 같은 실행 중에 재연결이 발생해도 두 번 초기화되지 않는다.

**결과 요약**

| 상황 | 동작 |
|------|------|
| SwarmUI 최초 시작 | ComfyUI history + queue clear → 대기열 패널 빈 상태 |
| 네트워크 오류 후 백엔드 재연결 | `HasDoneStartupClear = true`이므로 clear 안 함 |
| SwarmUI 재시작 | 새 인스턴스이므로 `HasDoneStartupClear = false` → 다시 clear |

---

## 5. 갤러리 영구 저장 구조

### 핵심 파일

- `src/Accounts/UserOutputHistoryIndex.cs`
- `src/Accounts/SessionHandler.cs`
- `Data/Users.ldb`

### 동작

갤러리 항목은 ComfyUI의 history와 완전히 별개다.  
`Comfy Workflow`에서 작업이 완료되면 SwarmUI는 결과 파일을 자신의 output 폴더에 복사하고 `LiteDB`에 인덱스 항목을 기록한다.

```csharp
// UserOutputHistoryIndex.cs
public class OutputEntry
{
    public string ID;        // "out_{Guid}"
    public string UserID;    // SwarmUI 사용자 ID
    public string DisplayPath;
    public string LocalPath; // 실제 파일 절대 경로
    public string WebPath;
    public string Metadata;
    public long   CreatedAt; // Unix timestamp
}
```

이 인덱스는 `Data/Users.ldb` 파일(LiteDB 컬렉션 `output_history_entries`)에 저장된다.

```csharp
// SessionHandler.cs
OutputHistoryEntries = Database.GetCollection<UserOutputHistoryIndex.OutputEntry>("output_history_entries");
```

`Data/Users.ldb`는 SwarmUI의 사용자 계정 DB와 같은 파일이다.  
이 파일은 SwarmUI 폴더 자체를 삭제하거나 수동으로 지우지 않는 한 유지된다.

### ComfyUI history와의 관계

| 항목 | 저장 위치 | 초기화 시점 |
|------|-----------|-------------|
| ComfyUI queue/history | ComfyUI 프로세스 메모리 | SwarmUI 시작 시 자동 clear |
| SwarmUI 갤러리 인덱스 | `Data/Users.ldb` | 사용자가 직접 삭제하거나 DB 파일을 지울 때 |
| 실제 output 이미지 파일 | `Output/local/comfy-workflow-*.png` | 파일 시스템에서 직접 삭제할 때 |

---

## 6. 갤러리 데이터 흐름 전체

```
[Comfy Workflow 실행]
        │
        ▼
ComfyUI 백엔드가 이미지 생성
        │
        ▼
SwarmUI가 GET /history/{prompt_id} 로 결과 폴링
        │
        ▼
결과 파일을 SwarmUI Output 폴더로 복사
(Output/local/comfy-workflow-{date}-{seq}-{batch}.png)
        │
        ▼
UserOutputHistoryIndex.RecordOutput() 호출
→ Data/Users.ldb 의 output_history_entries 에 항목 추가
        │
        ▼
사용자가 Gallery 탭 열기
→ ListIndexedImages API 호출
→ output_history_entries 조회 (현재 UserID 기준)
→ OutputIndex/{entryId} 라우트로 파일 서빙
→ 브라우저에 카드 표시
```

---

## 7. 정리

- **사용자별 대기열 분리**: SwarmUI가 `prompt_id` 소유권을 인메모리로 추적하고, ComfyUI 응답을 프록시할 때 필터링한다.
- **삭제 보호**: 자기 소유 `prompt_id`만 삭제 요청에 통과시킨다.
- **대기열 초기화**: SwarmUI 재시작 시 ComfyUI history/queue를 clear한다. 소유권 정보가 인메모리라 재시작 후 복원 불가능하므로 초기화하는 것이 일관성 있는 처리다.
- **갤러리 영구성**: 갤러리는 LiteDB와 파일 시스템에 저장되므로 SwarmUI를 재시작해도 유지된다.
