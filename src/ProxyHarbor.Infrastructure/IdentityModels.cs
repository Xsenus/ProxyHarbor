using Microsoft.AspNetCore.Identity;

namespace ProxyHarbor.Infrastructure;

/// <summary>Постоянная учётная запись оператора или клиента ProxyHarbor.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Имя, показываемое в кабинете независимо от логина.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Момент создания учётной записи в UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последний успешный вход; используется для аудита аккаунта.</summary>
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>Отключённая учётная запись не может получить новую сессию.</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>Текущая коммерческая подписка и набор будущих entitlement-прав.</summary>
    public UserSubscription? Subscription { get; set; }
}

/// <summary>Тариф пользователя, отделённый от роли для будущего биллинга.</summary>
public sealed class UserSubscription
{
    /// <summary>Идентификатор подписки.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Владелец; одна учётная запись имеет не более одной текущей подписки.</summary>
    public Guid UserId { get; set; }
    /// <summary>Навигация к владельцу.</summary>
    public ApplicationUser User { get; set; } = null!;
    /// <summary>Стабильный код тарифа: free, pro или unlimited.</summary>
    public string Plan { get; set; } = SubscriptionPlans.Free;
    /// <summary>Состояние: active, trialing, past_due, canceled или expired.</summary>
    public string Status { get; set; } = SubscriptionStatuses.Active;
    /// <summary>Начало текущего периода.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Конец доступа; null применяется для бесплатного и бессрочного тарифа.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Идентификатор клиента во внешней платёжной системе без платёжных данных.</summary>
    public string? ExternalCustomerId { get; set; }
    /// <summary>Идентификатор подписки во внешней платёжной системе.</summary>
    public string? ExternalSubscriptionId { get; set; }
    /// <summary>Последнее изменение подписки.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Стабильные системные роли; UI никогда не определяет права самостоятельно.</summary>
public static class UserRoles
{
    /// <summary>Базовая роль каждой зарегистрированной учётной записи.</summary>
    public const string User = "User";
    /// <summary>Роль активного платного клиента.</summary>
    public const string Subscriber = "Subscriber";
    /// <summary>Полный доступ к операционному управлению сервисом.</summary>
    public const string Administrator = "Administrator";
    /// <summary>Полный разрешённый набор ролей.</summary>
    public static readonly string[] All = [User, Subscriber, Administrator];
}

/// <summary>Коды тарифов, пригодные для интеграции с будущим billing provider.</summary>
public static class SubscriptionPlans
{
    /// <summary>Бесплатный тариф с будущими публичными лимитами.</summary>
    public const string Free = "free";
    /// <summary>Платный тариф с расширенными лимитами.</summary>
    public const string Pro = "pro";
    /// <summary>Тариф без ограничений каталога.</summary>
    public const string Unlimited = "unlimited";
    /// <summary>Полный разрешённый набор тарифов.</summary>
    public static readonly string[] All = [Free, Pro, Unlimited];
}

/// <summary>Разрешённые состояния жизненного цикла подписки.</summary>
public static class SubscriptionStatuses
{
    /// <summary>Оплаченный или бесплатный доступ действует.</summary>
    public const string Active = "active";
    /// <summary>Действует пробный период.</summary>
    public const string Trialing = "trialing";
    /// <summary>Оплата просрочена и требует обработки billing worker.</summary>
    public const string PastDue = "past_due";
    /// <summary>Автопродление отменено.</summary>
    public const string Canceled = "canceled";
    /// <summary>Срок доступа закончился.</summary>
    public const string Expired = "expired";
    /// <summary>Полный разрешённый набор состояний.</summary>
    public static readonly string[] All = [Active, Trialing, PastDue, Canceled, Expired];
}
