# Cost-to-Serve demo data (ILLUSTRATIVE)

This data is entirely made up for a demo walk-through. It is **not Walmart data** and contains no internal or customer information.

## What it covers
- **Period:** 42 days, Sep 14 – Oct 25, 2026, plus a 13-week fuel outlook starting Oct 26.
- **Geography:** 3 regions (Southeast, Midwest, West) and 20 zones (Urban, Suburban, Rural).
- **Delivery types:** Pickup, Same-day, Express (under 3 hours), Shipped.
- **Story built into the data:** diesel jumps about $0.40–0.45/gal from Oct 19. Southeast Express cost per order then rises about **+9.3%** week over week. The rise breaks down as roughly **57% fuel price, 34% fewer orders per route and 10% more split shipments**.

## Tables

| File | Grain | Key fields |
|---|---|---|
| zones.csv | zone | region, zone, density, miles_to_store |
| orders_daily.csv | date × zone × type | orders, avg_basket_usd, split_shipment_rate |
| routes_daily.csv | date × zone × type (Same-day and Express only) | routes, miles, stops, orders_per_route, miles_per_order |
| fuel_prices_daily.csv | date × region | diesel_usd_gal, gasoline_usd_gal |
| fuel_outlook_weekly.csv | week × region | diesel_low, diesel_base, diesel_high |
| market_events.csv | event | date, event_type (generic), region, expected_diesel_effect_usd_gal, credibility |
| carrier_invoices_weekly.csv | week × region × carrier | base_charge_usd, fuel_surcharge_pct, contract_index_surcharge_pct |
| cost_per_order_daily.csv | date × zone × type | fuel, labor, carrier, vehicle cost (USD), cost_per_order_usd |
| action_playbook.csv | action | lever, effect, customer_impact, approver, guardrail |
| decision_log.csv | decision | empty template for approvals |

## Cost model (assumptions)

**Same-day and Express**
- fuel per order = miles per order × (diesel ÷ 12 mpg + 0.16 × (diesel − $3.00)) × (1 + split rate)
- labor per order = route hours × $28 ÷ orders per route × (1 + 0.5 × split rate). Route hours: 3.0 for Same-day, 1.15 for Express.
- vehicle per order = miles per order × $0.55 × (1 + split rate)

**Pickup**
- $2.55 labor + $0.55 overhead + a small fuel-linked amount

**Shipped (carrier)**
- base charge = $5.45 × (1 + 0.6 × split rate)
- fuel surcharge = 10% + 6% × (diesel − $3.50)
- Carrier B bills about 1.2 points above the contract index. This is used for the "surcharge cap" lever.

## Scale
About 9.1M orders a week across the demo network, roughly $66M of weekly delivery cost. The numbers are internally consistent, but they are not a real network.
