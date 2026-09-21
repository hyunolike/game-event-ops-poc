namespace CouponOps.Domain;

/// <summary>이벤트 상태. 저장되지 않고 <see cref="CouponEvent.StatusAt"/>에서 파생된다.</summary>
public enum EventStatus : byte { Scheduled = 0, Active = 1, Ended = 2, Suspended = 3 }

/// <summary>쿠폰 코드 확보 방식. 설계 문서 1장 참조.</summary>
public enum IssuanceMode : byte
{
    /// <summary>이벤트 생성 시 코드를 미리 만들어 둔다. 발급은 코드 풀에서 LPOP 한 번.</summary>
    PreGenerated = 0,

    /// <summary>발급 시점에 시퀀스로부터 코드를 파생한다. 시퀀스가 유일하므로 충돌이 없다.</summary>
    Derived = 1,
}

public enum CouponStatus : byte { Unissued = 0, Issued = 1, Used = 2, Revoked = 3 }

public enum AdminRole : byte { Viewer = 0, Editor = 1 }

/// <summary>발급 시도의 결과. 실패 사유를 구분해 이력에 남긴다.</summary>
public enum IssueResult : byte
{
    Success = 1,
    /// <summary>수량 소진.</summary>
    SoldOut = 2,
    /// <summary>이벤트 기간 외.</summary>
    OutOfPeriod = 3,
    /// <summary>유저당 발급 한도 초과.</summary>
    LimitExceeded = 4,
    /// <summary>이미 처리된 RequestId 의 재시도. 최초 발급 결과를 그대로 돌려준다.</summary>
    DuplicateRequest = 5,
    /// <summary>운영자가 강제 중단한 이벤트.</summary>
    Suspended = 6,
    SystemError = 99,
}

public enum OperationAction : byte
{
    EventCreated = 0, EventUpdated = 1, EventSuspended = 2, EventResumed = 3,
    CouponPoolWarmed = 4, CouponRevoked = 5, AdminSignedIn = 6,

    // 룰렛(확률 지급) 이벤트. 값을 append 만 한다 — 기존 값이 밀리면 이미 적재된 감사 로그의 의미가 바뀐다.
    DrawEventCreated = 7, DrawEventUpdated = 8, DrawEventSuspended = 9, DrawEventResumed = 10,
    /// <summary>가중치(확률) 새 버전 활성화. 진행 중 변경은 공시와 직결되므로 별도 액션으로 남긴다.</summary>
    DrawWeightVersionActivated = 11,
    DrawPoolWarmed = 12,
    DrawTicketsGranted = 13,
    /// <summary>미수령 보상 우편 회수. 이미 수령한 우편은 대상이 아니다.</summary>
    DrawRewardsRevoked = 14,

    // 2인 승인(maker-checker). 요청·승인·반려를 따로 남겨야 "누가 올리고 누가 통과시켰는가" 가 보인다.
    DrawWeightChangeRequested = 15,
    DrawWeightChangeApproved = 16,
    DrawWeightChangeRejected = 17,
    DrawWeightChangeCancelled = 18,
}
