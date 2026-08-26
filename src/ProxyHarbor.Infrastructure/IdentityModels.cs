using Microsoft.AspNetCore.Identity;

namespace ProxyHarbor.Infrastructure;

/// <summary>Постоянная учётная запись оператора или клиента ProxyHarbor.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Имя, показываемое в кабинете независимо от логина.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Язык сайта, API, писем и привязанного Telegram-бота.</summary>
    public string PreferredLanguage { get; set; } = SupportedLanguages.Default;
    /// <summary>Момент создания учётной записи в UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последний успешный вход; используется для аудита аккаунта.</summary>
    public DateTimeOffset? LastLoginAt { get; set; }
    /// <summary>Отключённая учётная запись не может получить новую сессию.</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>Текущая коммерческая подписка и набор будущих entitlement-прав.</summary>
    public UserSubscription? Subscription { get; set; }
    /// <summary>Привязанный личный кабинет Telegram, если пользователь запускал торгового бота.</summary>
    public TelegramChat? TelegramChat { get; set; }
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

/// <summary>Неизменяемая запись ручного изменения подписки администратором.</summary>
public sealed class SubscriptionAdminAction
{
    /// <summary>Идентификатор события.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Изменённая подписка.</summary>
    public Guid SubscriptionId { get; set; }
    /// <summary>Навигация к подписке.</summary>
    public UserSubscription Subscription { get; set; } = null!;
    /// <summary>Администратор, выполнивший действие.</summary>
    public Guid AdministratorId { get; set; }
    /// <summary>Навигация к администратору.</summary>
    public ApplicationUser Administrator { get; set; } = null!;
    /// <summary>Код действия.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>Тариф до изменения.</summary>
    public string PreviousPlan { get; set; } = string.Empty;
    /// <summary>Статус до изменения.</summary>
    public string PreviousStatus { get; set; } = string.Empty;
    /// <summary>Срок до изменения.</summary>
    public DateTimeOffset? PreviousExpiresAt { get; set; }
    /// <summary>Тариф после изменения.</summary>
    public string NewPlan { get; set; } = string.Empty;
    /// <summary>Статус после изменения.</summary>
    public string NewStatus { get; set; } = string.Empty;
    /// <summary>Срок после изменения.</summary>
    public DateTimeOffset? NewExpiresAt { get; set; }
    /// <summary>Причина ручного изменения.</summary>
    public string? Reason { get; set; }
    /// <summary>Время действия в UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Пятиминутный агрегат выдачи прокси и посещений сайта. Он сохраняет
/// наблюдаемость без создания отдельной строки на каждый публичный запрос.
/// </summary>
public sealed class ProxyAccessBucket
{
    /// <summary>Суррогатный ключ агрегата.</summary>
    public long Id { get; set; }
    /// <summary>Начало пятиминутного окна.</summary>
    public DateTimeOffset BucketStartedAt { get; set; }
    /// <summary>Канонический адрес клиента после доверенного reverse proxy.</summary>
    public string IpAddress { get; set; } = string.Empty;
    /// <summary>Аккаунт либо null для анонимного клиента.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Навигация к аккаунту.</summary>
    public ApplicationUser? User { get; set; }
    /// <summary>Стабильная группа endpoint: catalog, export либо нормализованная page:*.</summary>
    public string Endpoint { get; set; } = string.Empty;
    /// <summary>Всего запросов.</summary>
    public int Requests { get; set; }
    /// <summary>Запросов, остановленных правилом блокировки.</summary>
    public int BlockedRequests { get; set; }
    /// <summary>Приблизительное число выданных адресов.</summary>
    public long ProxyItems { get; set; }
    /// <summary>Известный объём ответа в байтах.</summary>
    public long BytesSent { get; set; }
    /// <summary>Последнее обращение внутри окна.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// Постоянный маркер последней бесплатной выгрузки. Отдельная запись нужна, чтобы
/// десятиминутный интервал нельзя было обойти перезапуском API или параллельными запросами.
/// </summary>
public sealed class FreeProxyExportGrant
{
    /// <summary>Стабильный ключ аккаунта, а для гостя — канонического IP-адреса.</summary>
    public string ClientKey { get; set; } = string.Empty;
    /// <summary>Момент последней разрешённой бесплатной выгрузки.</summary>
    public DateTimeOffset LastGrantedAt { get; set; }
    /// <summary>Первый момент, когда следующая бесплатная выгрузка снова разрешена.</summary>
    public DateTimeOffset NextAllowedAt { get; set; }
}

/// <summary>Административное правило блокировки IP/CIDR или пользователя.</summary>
public sealed class AccessBlockRule
{
    /// <summary>Идентификатор правила.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Тип цели: ip, cidr или user.</summary>
    public string Kind { get; set; } = AccessBlockKinds.Ip;
    /// <summary>Каноническое значение цели.</summary>
    public string Value { get; set; } = string.Empty;
    /// <summary>Аккаунт для user-правила.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Навигация к заблокированному аккаунту.</summary>
    public ApplicationUser? User { get; set; }
    /// <summary>Обязательная причина для аудита.</summary>
    public string Reason { get; set; } = string.Empty;
    /// <summary>Активно ли правило.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Автоматическое окончание блокировки.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Создавший правило администратор.</summary>
    public Guid AdministratorId { get; set; }
    /// <summary>Навигация к администратору.</summary>
    public ApplicationUser Administrator { get; set; } = null!;
    /// <summary>Создание правила.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последнее изменение правила.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Закрытый набор типов правил контроля выдачи.</summary>
public static class AccessBlockKinds
{
    /// <summary>Один IP-адрес.</summary>
    public const string Ip = "ip";
    /// <summary>IP-подсеть в CIDR.</summary>
    public const string Cidr = "cidr";
    /// <summary>Зарегистрированный пользователь.</summary>
    public const string User = "user";
    /// <summary>Все допустимые типы.</summary>
    public static readonly string[] All = [Ip, Cidr, User];
}

/// <summary>Заказ на оплату подписки; карточные и банковские реквизиты здесь не хранятся.</summary>
public sealed class PaymentOrder
{
    /// <summary>Внутренний неизменяемый идентификатор заказа.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Владелец заказа.</summary>
    public Guid UserId { get; set; }
    /// <summary>Навигация к владельцу.</summary>
    public ApplicationUser User { get; set; } = null!;
    /// <summary>Код продукта из конфигурации каталога.</summary>
    public string ProductCode { get; set; } = string.Empty;
    /// <summary>Тариф, который будет активирован только после подтверждённой оплаты.</summary>
    public string Plan { get; set; } = SubscriptionPlans.Pro;
    /// <summary>Платёжный шлюз либо встроенный канал telegram_stars.</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>Сумма в минимальных единицах валюты, например копейках.</summary>
    public long AmountMinor { get; set; }
    /// <summary>Трёхбуквенный ISO 4217 код валюты.</summary>
    public string Currency { get; set; } = "RUB";
    /// <summary>Продолжительность оплаченного доступа.</summary>
    public int DurationDays { get; set; }
    /// <summary>pending, paid, failed, canceled или refunded.</summary>
    public string Status { get; set; } = PaymentStatuses.Pending;
    /// <summary>Идентификатор операции на стороне шлюза.</summary>
    public string? ProviderPaymentId { get; set; }
    /// <summary>Hosted checkout URL; номера карт никогда не проходят через ProxyHarbor.</summary>
    public string? CheckoutUrl { get; set; }
    /// <summary>Ключ защиты повторной отправки checkout-запроса.</summary>
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Создание заказа в UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Момент подтверждения оплаты шлюзом.</summary>
    public DateTimeOffset? PaidAt { get; set; }
    /// <summary>Последняя синхронизация состояния.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Строго ограниченные состояния платёжного заказа.</summary>
public static class PaymentStatuses
{
    /// <summary>Ожидается действие пользователя или подтверждение шлюза.</summary>
    public const string Pending = "pending";
    /// <summary>Оплата окончательно подтверждена.</summary>
    public const string Paid = "paid";
    /// <summary>Шлюз сообщил об ошибке операции.</summary>
    public const string Failed = "failed";
    /// <summary>Оплата отменена до завершения.</summary>
    public const string Canceled = "canceled";
    /// <summary>Деньги возвращены плательщику.</summary>
    public const string Refunded = "refunded";
    /// <summary>Все допустимые состояния.</summary>
    public static readonly string[] All = [Pending, Paid, Failed, Canceled, Refunded];
}

/// <summary>
/// Единственная runtime-конфигурация биллинга. Открытые настройки хранятся JSON,
/// а реквизиты провайдеров — только в защищённом Data Protection контейнере.
/// </summary>
public sealed class PaymentConfiguration
{
    /// <summary>Фиксированный ключ singleton-записи.</summary>
    public int Id { get; set; } = 1;
    /// <summary>Версионируемые несекретные настройки продуктов и провайдеров.</summary>
    public string SettingsJson { get; set; } = string.Empty;
    /// <summary>Зашифрованный JSON с ключами и паролями провайдеров.</summary>
    public string ProtectedSecrets { get; set; } = string.Empty;
    /// <summary>Момент последнего административного изменения.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Singleton-конфигурация торгового Telegram-бота с зашифрованным token.</summary>
public sealed class TelegramBotConfiguration
{
    /// <summary>Фиксированный ключ единственной записи.</summary>
    public int Id { get; set; } = 1;
    /// <summary>Версионируемые публичные настройки бота и цены в Stars.</summary>
    public string SettingsJson { get; set; } = string.Empty;
    /// <summary>Data Protection ciphertext с bot token и webhook secret.</summary>
    public string ProtectedSecrets { get; set; } = string.Empty;
    /// <summary>Telegram ID проверенного бота.</summary>
    public long? BotId { get; set; }
    /// <summary>Username проверенного бота без раскрытия token.</summary>
    public string? BotUsername { get; set; }
    /// <summary>Последняя успешная автоматическая настройка профиля и команд.</summary>
    public DateTimeOffset? ProvisionedAt { get; set; }
    /// <summary>Последнее изменение конфигурации.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Приватный Telegram-диалог и связанный пользователь ProxyHarbor.</summary>
public sealed class TelegramChat
{
    /// <summary>Внутренний идентификатор.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Telegram chat_id приватного чата.</summary>
    public long ChatId { get; set; }
    /// <summary>Telegram user id; хранится как Int64 по контракту Bot API.</summary>
    public long TelegramUserId { get; set; }
    /// <summary>Автоматически созданная или позднее связанная учётная запись.</summary>
    public Guid UserId { get; set; }
    /// <summary>Навигация к учётной записи.</summary>
    public ApplicationUser User { get; set; } = null!;
    /// <summary>Публичный username Telegram, если он задан.</summary>
    public string? Username { get; set; }
    /// <summary>Имя из Telegram для CRM.</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Язык клиента из Telegram.</summary>
    public string? LanguageCode { get; set; }
    /// <summary>Разрешены ли сервисные напоминания и информационные рассылки.</summary>
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>Пользователь заблокировал бота либо чат отключён администратором.</summary>
    public bool IsBlocked { get; set; }
    /// <summary>Первое взаимодействие.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Последнее входящее взаимодействие.</summary>
    public DateTimeOffset LastInteractionAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Идемпотентный журнал входящих Telegram update для webhook и polling.</summary>
public sealed class TelegramUpdateReceipt
{
    /// <summary>Глобальный update_id Telegram.</summary>
    public long UpdateId { get; set; }
    /// <summary>Источник доставки: webhook или polling.</summary>
    public string Transport { get; set; } = string.Empty;
    /// <summary>Момент принятия update.</summary>
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Момент успешной обработки.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }
    /// <summary>Ограниченная диагностическая ошибка последней попытки.</summary>
    public string? Error { get; set; }
}

/// <summary>Надёжная серверная очередь исходящих сообщений Telegram.</summary>
public sealed class TelegramOutboundMessage
{
    /// <summary>Идентификатор задания.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Получатель.</summary>
    public Guid TelegramChatId { get; set; }
    /// <summary>Навигация к получателю.</summary>
    public TelegramChat TelegramChat { get; set; } = null!;
    /// <summary>text, invoice или proxy_file.</summary>
    public string Kind { get; set; } = TelegramOutboundKinds.Text;
    /// <summary>JSON payload без секретов.</summary>
    public string PayloadJson { get; set; } = string.Empty;
    /// <summary>Уникальный бизнес-ключ, предотвращающий повтор напоминания или broadcast.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;
    /// <summary>pending, processing, sent, failed или canceled.</summary>
    public string Status { get; set; } = TelegramOutboundStatuses.Pending;
    /// <summary>Количество сетевых попыток.</summary>
    public int Attempts { get; set; }
    /// <summary>Не отправлять раньше указанного времени.</summary>
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Lease нескольких реплик worker.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }
    /// <summary>message_id после подтверждения Telegram.</summary>
    public long? TelegramMessageId { get; set; }
    /// <summary>Ограниченная последняя ошибка.</summary>
    public string? LastError { get; set; }
    /// <summary>Создание задания.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Успешная отправка.</summary>
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Сообщение CRM-диалога; сохраняется отдельно от транспортной очереди.</summary>
public sealed class TelegramConversationMessage
{
    /// <summary>Идентификатор сообщения.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Диалог.</summary>
    public Guid TelegramChatId { get; set; }
    /// <summary>Навигация к диалогу.</summary>
    public TelegramChat TelegramChat { get; set; } = null!;
    /// <summary>inbound, bot или admin.</summary>
    public string Direction { get; set; } = string.Empty;
    /// <summary>Текст, отображаемый в CRM.</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>Администратор для ручного ответа.</summary>
    public Guid? AdministratorId { get; set; }
    /// <summary>Навигация к администратору.</summary>
    public ApplicationUser? Administrator { get; set; }
    /// <summary>Связанное исходящее задание.</summary>
    public Guid? OutboundMessageId { get; set; }
    /// <summary>Время сообщения.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Допустимые виды заданий Telegram-очереди.</summary>
public static class TelegramOutboundKinds
{
    /// <summary>Текстовое сообщение.</summary>
    public const string Text = "text";
    /// <summary>Счёт Telegram Stars.</summary>
    public const string Invoice = "invoice";
    /// <summary>Сформированный TXT-файл прокси.</summary>
    public const string ProxyFile = "proxy_file";
    /// <summary>Полный допустимый набор.</summary>
    public static readonly string[] All = [Text, Invoice, ProxyFile];
}

/// <summary>Состояния надёжной Telegram-очереди.</summary>
public static class TelegramOutboundStatuses
{
    /// <summary>Ожидает обработки.</summary>
    public const string Pending = "pending";
    /// <summary>Арендовано worker.</summary>
    public const string Processing = "processing";
    /// <summary>Подтверждено Telegram.</summary>
    public const string Sent = "sent";
    /// <summary>Исчерпаны повторы либо ошибка постоянная.</summary>
    public const string Failed = "failed";
    /// <summary>Отменено из-за блокировки чата.</summary>
    public const string Canceled = "canceled";
    /// <summary>Полный допустимый набор.</summary>
    public static readonly string[] All = [Pending, Processing, Sent, Failed, Canceled];
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
    /// <summary>Доступ вручную приостановлен администратором.</summary>
    public const string Suspended = "suspended";
    /// <summary>Полный разрешённый набор состояний.</summary>
    public static readonly string[] All = [Active, Trialing, PastDue, Canceled, Expired, Suspended];
}
