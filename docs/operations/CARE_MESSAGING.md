# Care+ messaging — connecting real channels (owner decision required)

Default: **sandbox**. Messages are planned, recorded and visible in Care+ → Messages, but nothing leaves the system.

## Telegram (implemented, off by default)
1. Create a bot with @BotFather; note the token and username. Keep the token secret.
2. Set environment variables (Railway → service → Variables, or your secret store):
   `Care__Telegram__Enabled=true`, `Care__Telegram__BotToken=<token>`, `Care__Telegram__BotUsername=<username>`,
   `Care__Telegram__WebhookSecret=<random ≥ 24 chars>`.
3. Register the webhook once:
   `https://api.telegram.org/bot<token>/setWebhook?url=https://<your-domain>/api/v1/care/telegram/webhook&secret_token=<secret>`
4. Test with a synthetic patient: Reception → Link Telegram → send `/start CODE` to the bot → the chat is linked.
The webhook answers 404 unless Telegram is enabled and the secret matches.

## SMS (not connected)
Choose a licensed gateway (e.g. an Ethio telecom bulk-SMS aggregator), agree a sender ID and costs, then implement an
`ICareMessenger` for it. Until then SMS stays in the sandbox. Amharic/Tigrinya SMS use 70-character parts (cost).

## Before going live
- Owner approval and any provider contract/costs.
- Patient privacy notice and consent wording reviewed (Ethiopian Personal Data Protection Proclamation No. 1321/2024).
- Clinical review of templates, the reply word lists (all three languages) and health tips.
- Someone assigned to the care desk every working day; urgent replies are due immediately.
- Demo only: `Care__AllowSimulation=true` enables "Simulate reply" (ignored outside Development/Testing/Demo).
