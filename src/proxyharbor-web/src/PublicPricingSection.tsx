import { ArrowRight, Check } from "lucide-react";
import { useEffect, useState } from "react";

type PaymentProduct = {
  code: string;
  name: string;
  durationDays: number;
  amountMinor: number;
  discountPercent: number;
  fullDailyPriceMinor: number;
  savingsMinor: number;
  currency: string;
  description: string;
};

type PaymentCatalog = { enabled: boolean; products: PaymentProduct[] };

/**
 * Тарифы используются и на главной, и на отдельной информационной странице.
 * Компонент намеренно отделён от объёмных юридических документов: благодаря
 * этому главная страница не загружает их код до фактического перехода.
 */
export function PublicPricingSection({
  apiBaseUrl,
  compact = false,
}: {
  apiBaseUrl: string;
  compact?: boolean;
}) {
  const [catalog, setCatalog] = useState<PaymentCatalog | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    void fetch(`${apiBaseUrl}/api/v1/payments/catalog`, {
      signal: controller.signal,
    })
      .then(async (response) => {
        if (!response.ok) throw new Error("Тарифы временно недоступны");
        setCatalog((await response.json()) as PaymentCatalog);
      })
      .catch((reason) => {
        if (reason instanceof DOMException && reason.name === "AbortError") return;
        setError("Тарифы временно недоступны");
      });

    return () => controller.abort();
  }, [apiBaseUrl]);

  const products = [...(catalog?.products ?? [])].sort(
    (left, right) => left.durationDays - right.durationDays,
  );

  return (
    <section
      className={`public-pricing${compact ? " compact" : ""}`}
      id="pricing"
      aria-labelledby="public-pricing-title"
    >
      <div className="public-section-heading">
        <span>ТАРИФЫ</span>
        <h2 id="public-pricing-title">Полный доступ к ProxyHarbor</h2>
        <p>
          Один тариф Unlimited с разным сроком действия. Оплата разовая,
          автоматического продления и скрытых списаний нет.
        </p>
      </div>
      {error && <p className="public-pricing-error">{error}</p>}
      {!catalog && !error && (
        <p className="public-pricing-loading">Загружаем актуальные цены…</p>
      )}
      <div className="public-price-grid">
        {products.map((product) => (
          <article
            key={product.code}
            className={product.durationDays === 30 ? "featured" : ""}
          >
            {product.durationDays === 30 && <em>Популярный</em>}
            <small>
              {product.durationDays} {dayWord(product.durationDays)}
            </small>
            <h3>{product.name}</h3>
            <strong>{rubles(product.amountMinor)}</strong>
            <p>{product.description}</p>
            <ul>
              <li>
                <Check />
                Все доступные прокси и VPN
              </li>
              <li>
                <Check />
                Полные API-ответы и экспорт
              </li>
              <li>
                <Check />
                Доступ сразу после подтверждения оплаты
              </li>
            </ul>
            {product.savingsMinor > 0 && (
              <span>
                <s>{rubles(product.fullDailyPriceMinor)}</s> · экономия{" "}
                {rubles(product.savingsMinor)}
              </span>
            )}
            <a href="/register">
              Выбрать тариф <ArrowRight />
            </a>
          </article>
        ))}
      </div>
      {products.length > 0 && (
        <p className="public-price-note">
          Цены окончательные и указаны в рублях. Для оформления необходимо
          создать аккаунт; доступ активируется на оплаченный срок.
        </p>
      )}
    </section>
  );
}

function rubles(value: number) {
  return new Intl.NumberFormat("ru-RU", {
    style: "currency",
    currency: "RUB",
    maximumFractionDigits: 0,
  }).format(value / 100);
}

function dayWord(days: number) {
  const lastTwo = days % 100;
  const last = days % 10;
  return lastTwo >= 11 && lastTwo <= 14
    ? "дней"
    : last === 1
      ? "день"
      : last >= 2 && last <= 4
        ? "дня"
        : "дней";
}
