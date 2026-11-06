# Finance.Pull

Hourly pull of Monzo transactions into daily CSV files (Europe/London days), for a personal life-admin workspace.

- `dotnet run --project Finance.Pull -- auth` : one-off OAuth sign-in (needs a **Confidential** client from developers.monzo.com, redirect `http://localhost:8765/callback`). Approve API access in the Monzo app afterwards.
- `dotnet run --project Finance.Pull -- pull` : writes `monzo-<personal|joint>-YYYY-MM-DD.csv` for today and yesterday to `LIFE_INBOX`, and a `status.json`.
- `dotnet run --project Finance.Pull -- selftest` : writes sample files, no network.

Secrets (client id/secret, tokens) are DPAPI-encrypted in `%LOCALAPPDATA%\LifeFinance\monzo.bin`. Nothing sensitive belongs in this repo.
Schedule `pull` hourly with Windows Task Scheduler.
