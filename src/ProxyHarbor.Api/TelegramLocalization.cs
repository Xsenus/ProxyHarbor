using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.Api;

/// <summary>Тексты Telegram отделены от сценариев, чтобы новые языки не меняли бизнес-логику бота.</summary>
internal static class TelegramLocalization
{
    private static readonly Dictionary<string, string[]> Texts = new(StringComparer.Ordinal)
    {
        ["languageSaved"] = ["Язык изменён на русский.", "Language changed to English.", "Sprache auf Deutsch geändert.", "Langue changée en français.", "语言已切换为简体中文。"],
        ["chooseLanguage"] = ["<b>Выберите язык</b>\nОн будет использоваться на сайте, в письмах и в этом боте.", "<b>Choose a language</b>\nIt will be used on the website, in emails, and in this bot.", "<b>Sprache auswählen</b>\nSie wird auf der Website, in E-Mails und in diesem Bot verwendet.", "<b>Choisissez une langue</b>\nElle sera utilisée sur le site, dans les e-mails et dans ce bot.", "<b>选择语言</b>\n该语言将用于网站、邮件和此机器人。"],
        ["main"] = ["<b>ProxyHarbor</b>\nПроверенные публичные прокси, покупка подписки и выгрузка файлов прямо в Telegram.", "<b>ProxyHarbor</b>\nVerified public proxies, subscriptions, and file downloads directly in Telegram.", "<b>ProxyHarbor</b>\nGeprüfte öffentliche Proxys, Abonnements und Downloads direkt in Telegram.", "<b>ProxyHarbor</b>\nProxys publics vérifiés, abonnements et téléchargements directement dans Telegram.", "<b>ProxyHarbor</b>\n在 Telegram 中直接获取已验证的公开代理、购买订阅并下载文件。"],
        ["accountButton"] = ["👤 Личный кабинет", "👤 Account", "👤 Konto", "👤 Compte", "👤 个人中心"],
        ["buyButton"] = ["⭐ Купить", "⭐ Buy", "⭐ Kaufen", "⭐ Acheter", "⭐ 购买"],
        ["proxyButton"] = ["📄 Получить прокси", "📄 Get proxies", "📄 Proxys abrufen", "📄 Obtenir les proxys", "📄 获取代理"],
        ["notificationsButton"] = ["🔔 Уведомления", "🔔 Notifications", "🔔 Benachrichtigungen", "🔔 Notifications", "🔔 通知"],
        ["languageButton"] = ["🌐 Язык", "🌐 Language", "🌐 Sprache", "🌐 Langue", "🌐 语言"],
        ["choosePlanButton"] = ["⭐ Выбрать тариф", "⭐ Choose a plan", "⭐ Tarif wählen", "⭐ Choisir une offre", "⭐ 选择套餐"],
        ["products"] = ["<b>Выберите подписку</b>\nЦена окончательная и списывается в Telegram Stars только после подтверждения.", "<b>Choose a subscription</b>\nThe final price is charged in Telegram Stars only after confirmation.", "<b>Abonnement auswählen</b>\nDer Endpreis wird erst nach Bestätigung in Telegram Stars belastet.", "<b>Choisissez un abonnement</b>\nLe prix final en Telegram Stars n'est débité qu'après confirmation.", "<b>选择订阅</b>\n确认后才会从 Telegram Stars 中扣除最终价格。"],
        ["productsEmpty"] = ["Продажи через Telegram временно приостановлены.", "Telegram sales are temporarily paused.", "Der Verkauf über Telegram ist vorübergehend pausiert.", "Les ventes via Telegram sont temporairement suspendues.", "Telegram 销售暂时暂停。"],
        ["productUnavailable"] = ["Этот тариф сейчас недоступен для оплаты в Telegram.", "This plan is currently unavailable in Telegram.", "Dieser Tarif ist derzeit in Telegram nicht verfügbar.", "Cette offre est actuellement indisponible dans Telegram.", "此套餐目前无法在 Telegram 中购买。"],
        ["proxyDenied"] = ["Для выгрузки нужна активная подписка. Выберите тариф через /buy.", "An active subscription is required. Choose a plan with /buy.", "Für den Download ist ein aktives Abonnement erforderlich. Tarif über /buy wählen.", "Un abonnement actif est requis. Choisissez une offre avec /buy.", "下载需要有效订阅。请通过 /buy 选择套餐。"],
        ["proxyQueued"] = ["Файл поставлен в очередь и придёт отдельным сообщением.", "The file is queued and will arrive in a separate message.", "Die Datei wurde eingereiht und kommt als separate Nachricht.", "Le fichier est en attente et arrivera dans un message séparé.", "文件已加入队列，将通过单独消息发送。"],
        ["notificationsOn"] = ["🔔 Уведомления о подписке и важных изменениях включены.", "🔔 Subscription and important update notifications are enabled.", "🔔 Benachrichtigungen zu Abonnement und wichtigen Änderungen sind aktiviert.", "🔔 Les notifications d'abonnement et de changements importants sont activées.", "🔔 已开启订阅和重要变更通知。"],
        ["notificationsOff"] = ["🔕 Информационные уведомления отключены. Чеки и ответы поддержки продолжат приходить.", "🔕 Informational notifications are off. Receipts and support replies will still arrive.", "🔕 Info-Benachrichtigungen sind aus. Belege und Support-Antworten kommen weiterhin.", "🔕 Les notifications d'information sont désactivées. Les reçus et réponses du support continueront d'arriver.", "🔕 信息通知已关闭。收据和客服回复仍会发送。"],
        ["help"] = ["<b>Помощь</b>\n/account — подписка\n/buy — оплата Stars\n/proxies — TXT-файл\n/notifications — уведомления\n/language — язык\n/support — поддержка", "<b>Help</b>\n/account — subscription\n/buy — pay with Stars\n/proxies — TXT file\n/notifications — notifications\n/language — language\n/support — support", "<b>Hilfe</b>\n/account — Abonnement\n/buy — mit Stars bezahlen\n/proxies — TXT-Datei\n/notifications — Benachrichtigungen\n/language — Sprache\n/support — Support", "<b>Aide</b>\n/account — abonnement\n/buy — payer avec Stars\n/proxies — fichier TXT\n/notifications — notifications\n/language — langue\n/support — assistance", "<b>帮助</b>\n/account — 订阅\n/buy — Stars 支付\n/proxies — TXT 文件\n/notifications — 通知\n/language — 语言\n/support — 客服"],
        ["noExpiry"] = ["без срока", "no expiry", "unbefristet", "sans expiration", "无期限"],
        ["enabled"] = ["включены", "enabled", "aktiviert", "activées", "已开启"],
        ["disabled"] = ["выключены", "disabled", "deaktiviert", "désactivées", "已关闭"],
        ["activeSubscription"] = ["✅ активна", "✅ active", "✅ aktiv", "✅ actif", "✅ 有效"],
        ["inactiveSubscription"] = ["⛔ нет активной подписки", "⛔ no active subscription", "⛔ kein aktives Abonnement", "⛔ aucun abonnement actif", "⛔ 无有效订阅"],
        ["account"] = ["<b>Личный кабинет</b>\nСтатус: {status}\nТариф: <b>{plan}</b>\nДействует до: <b>{expires}</b>\n\n<b>Статистика</b>\nОплат Stars: <b>{payments}</b> · всего <b>{stars} ⭐</b>\nПоследняя оплата: <b>{last}</b>\nПолучено файлов: <b>{files}</b>\nУведомления: {notifications}", "<b>Account</b>\nStatus: {status}\nPlan: <b>{plan}</b>\nValid until: <b>{expires}</b>\n\n<b>Statistics</b>\nStars payments: <b>{payments}</b> · total <b>{stars} ⭐</b>\nLast payment: <b>{last}</b>\nFiles received: <b>{files}</b>\nNotifications: {notifications}", "<b>Konto</b>\nStatus: {status}\nTarif: <b>{plan}</b>\nGültig bis: <b>{expires}</b>\n\n<b>Statistik</b>\nStars-Zahlungen: <b>{payments}</b> · gesamt <b>{stars} ⭐</b>\nLetzte Zahlung: <b>{last}</b>\nDateien erhalten: <b>{files}</b>\nBenachrichtigungen: {notifications}", "<b>Compte</b>\nStatut : {status}\nOffre : <b>{plan}</b>\nValable jusqu'au : <b>{expires}</b>\n\n<b>Statistiques</b>\nPaiements Stars : <b>{payments}</b> · total <b>{stars} ⭐</b>\nDernier paiement : <b>{last}</b>\nFichiers reçus : <b>{files}</b>\nNotifications : {notifications}", "<b>个人中心</b>\n状态：{status}\n套餐：<b>{plan}</b>\n有效期至：<b>{expires}</b>\n\n<b>统计</b>\nStars 支付：<b>{payments}</b> · 共 <b>{stars} ⭐</b>\n最近支付：<b>{last}</b>\n已收文件：<b>{files}</b>\n通知：{notifications}"],
        ["paymentConfirmed"] = ["✅ Оплата подтверждена. Тариф <b>{plan}</b> действует до <b>{expires}</b>. Файл доступен через /proxies.", "✅ Payment confirmed. Plan <b>{plan}</b> is valid until <b>{expires}</b>. Use /proxies to get the file.", "✅ Zahlung bestätigt. Tarif <b>{plan}</b> gilt bis <b>{expires}</b>. Datei über /proxies abrufen.", "✅ Paiement confirmé. L'offre <b>{plan}</b> est valable jusqu'au <b>{expires}</b>. Utilisez /proxies pour le fichier.", "✅ 支付已确认。套餐 <b>{plan}</b> 有效期至 <b>{expires}</b>。使用 /proxies 获取文件。"],
        ["supportForwarded"] = ["Сообщение передано оператору. {support}", "Your message was forwarded to support. {support}", "Ihre Nachricht wurde an den Support weitergeleitet. {support}", "Votre message a été transmis à l'assistance. {support}", "您的消息已转交客服。{support}"],
        ["faqPayment"] = ["Оплата выполняется встроенным счётом Telegram Stars. Нажмите /buy, выберите тариф и подтвердите списание.", "Payment uses a built-in Telegram Stars invoice. Select /buy, choose a plan, and confirm the charge.", "Die Zahlung erfolgt über eine integrierte Telegram-Stars-Rechnung. /buy wählen, Tarif auswählen und Belastung bestätigen.", "Le paiement utilise une facture Telegram Stars intégrée. Utilisez /buy, choisissez une offre et confirmez le débit.", "付款通过 Telegram Stars 内置账单完成。请使用 /buy，选择套餐并确认扣款。"],
        ["faqProxy"] = ["При активной подписке команда /proxies создаёт свежий TXT-файл с проверенными HTTP, HTTPS, SOCKS4 и SOCKS5 адресами.", "With an active subscription, /proxies creates a fresh TXT file containing verified HTTP, HTTPS, SOCKS4, and SOCKS5 addresses.", "Mit aktivem Abonnement erstellt /proxies eine aktuelle TXT-Datei mit geprüften HTTP-, HTTPS-, SOCKS4- und SOCKS5-Adressen.", "Avec un abonnement actif, /proxies crée un fichier TXT récent contenant des adresses HTTP, HTTPS, SOCKS4 et SOCKS5 vérifiées.", "订阅有效时，/proxies 会生成包含已验证 HTTP、HTTPS、SOCKS4 和 SOCKS5 地址的最新 TXT 文件。"],
        ["faqSpeed"] = ["ProxyHarbor регулярно перепроверяет доступность и задержку. В файл попадают только прокси с подтверждённым статусом Alive.", "ProxyHarbor regularly rechecks availability and latency. Only proxies with a confirmed Alive status are included in the file.", "ProxyHarbor prüft Verfügbarkeit und Latenz regelmäßig erneut. Die Datei enthält nur Proxys mit bestätigtem Alive-Status.", "ProxyHarbor revérifie régulièrement la disponibilité et la latence. Seuls les proxys dont le statut Alive est confirmé figurent dans le fichier.", "ProxyHarbor 会定期复查可用性和延迟。文件中仅包含已确认状态为 Alive 的代理。"],
        ["faqSubscription"] = ["Срок и тариф показаны в /account. Перед окончанием бот напомнит о продлении, если уведомления включены.", "Your plan and expiry date are shown in /account. If notifications are enabled, the bot will remind you before renewal is due.", "Tarif und Ablaufdatum stehen unter /account. Bei aktivierten Benachrichtigungen erinnert der Bot rechtzeitig an die Verlängerung.", "Votre offre et sa date d'expiration sont visibles dans /account. Si les notifications sont activées, le bot vous rappellera le renouvellement.", "套餐和到期时间可在 /account 中查看。若已开启通知，机器人会在到期前提醒续订。"],
    };

    public static string Get(string key, string? language, params (string Name, object? Value)[] variables)
    {
        var index = SupportedLanguages.Normalize(language) switch
        {
            SupportedLanguages.English => 1,
            SupportedLanguages.German => 2,
            SupportedLanguages.French => 3,
            SupportedLanguages.Chinese => 4,
            _ => 0
        };
        var text = Texts.TryGetValue(key, out var translations) ? translations[index] : key;
        foreach (var variable in variables)
            text = text.Replace($"{{{variable.Name}}}", Convert.ToString(variable.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return text;
    }
}
