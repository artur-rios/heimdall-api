# API client collections

Ready-to-send requests for every endpoint the API exposes, in two formats:

| Directory | Format | Opens in |
| --- | --- | --- |
| [`http/`](http) | JetBrains HTTP Client (`.http`) | Rider, IntelliJ IDEA, and VS Code with the REST Client extension |
| [`bruno/`](bruno) | [Bruno](https://www.usebruno.com) collection | The Bruno app, or `bru run` on the command line |

Both cover the same 49 operations — the same set the
[published OpenAPI document](../docs/openapi/heimdall.json) describes — and both are organised by
subject: authentication, scopes, persons, applications, scope permissions, Google Users, health.

For what each endpoint means and who may call it, see the
[API reference](https://artur-rios.github.io/heimdall-api/docs/api-reference/). To read the schemas,
see the [API explorer](https://artur-rios.github.io/heimdall-api/docs/api-explorer/), or a running
instance's own Swagger UI at `/swagger` — they publish the same document.

## Before the first request

Run the API locally — see [Getting started](https://artur-rios.github.io/heimdall-api/docs/getting-started/)
— and note two values from your `.env.local`:

- `HEIMDALL_MASTER_USER_EMAIL` and `HEIMDALL_MASTER_USER_PASSWORD`. The master user is seeded at
  startup and is the only account that exists on a fresh database, so it is the only one that can log
  in before anything else is created.
- The port. `http://localhost:5177` is the `http` launch profile, which both collections default to;
  the `https` profile is `https://localhost:7235`.

Put the credentials into the environment your client reads — `http/http-client.env.json` or
`bruno/environments/Local.bru` — replacing the `admin@example.com` placeholders.

Both environment files are tracked templates — see [Keeping secrets out of git](#keeping-secrets-out-of-git)
before putting real credentials in them.

## Order on an empty database

Requests that create something store the new id, and the rest of the collection reads those ids. On a
database with nothing in it, four requests have to run in this order before any other will find its
subject:

1. **Login** — stores the token every authenticated request sends.
2. **Create an administrator** — a `ScopeAdmin`, owning nothing yet. Stores `personId`.
3. **Create a scope**, owned by that person. Stores `scopeId`.
4. **Create a User in a scope**, a member of that scope. Stores `scopeUserId`.

Everything else can then be run in any order. The files and folders are ordered by subject rather
than by that sequence, because a scope is what the API is organised around and reading the collection
top to bottom should say so — these are a reference, not a test suite. Running the Bruno collection
end to end with `bru run -r` answers 404 for everything scoped until the four above have been run.

## How the chaining works

Neither collection asks you to copy a GUID by hand.

In the `.http` files, a response handler stores the value:

```
> {% client.global.set("scopeId", response.body.data.id); %}
```

and in Bruno, a post-response script does the same:

```
script:post-response {
  bru.setEnvVar("scopeId", res.body.data.id);
}
```

Both write to a place that takes precedence over the environment file, so the placeholder GUIDs there
are only ever what a variable reads *before* the request that fills it has run.

The one value that is not chained is a token that arrives by email — the password-reset and
email-verification tokens. Configure the logging sender (`EMAIL_DELIVERY=Logging`) and they are
written to the API's log rather than sent, which is where to copy them from locally.

## Running the Bruno collection from the command line

```bash
npx @usebruno/cli run -r --env Local
```

Individual folders work too, which is usually what you want given the ordering above:

```bash
npx @usebruno/cli run Auth --env Local
```

Credentials can be overridden per run rather than edited into the file:

```bash
npx @usebruno/cli run Auth --env Local --env-var adminEmail=you@example.com --env-var adminPassword=secret
```

## Rate limiting

Every anonymous endpoint under `/api/auth` — login, password recovery and reset, email verification,
Google sign-in, and two-factor verification — is limited to **10 requests per minute per IP**. A run
of the whole `Auth` folder sits just under that, so two runs in quick succession will answer 429. It
is the limiter working, not a broken request.

## Keeping secrets out of git

`http/http-client.env.json` and `bruno/environments/*.bru` are tracked templates holding placeholders,
so they must not be edited with real credentials against a shared environment. Both clients support a
private overlay that is gitignored:

- **JetBrains** reads `http/http-client.private.env.json`, which wins over the tracked file per key.
- **Bruno** reads `bruno/.env`, whose values are available as `{{process.env.NAME}}`.

Both are already in the repository's `.gitignore`.
