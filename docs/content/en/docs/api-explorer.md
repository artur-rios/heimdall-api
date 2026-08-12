+++
title = 'API explorer'
linkTitle = 'API explorer'
weight = 61
description = 'Swagger UI over the published OpenAPI document — every endpoint, its schema, and its authorization.'
+++

Every endpoint the API exposes, rendered from
[the published OpenAPI document](/heimdall-api/openapi/heimdall.json). The document is generated from
the controllers themselves — the summaries below are the ones in the source — and regenerated with
`python scripts/openapi.py`. It is byte-for-byte what a running instance serves at
`/swagger/v1/swagger.json`: both come from the same `SwaggerConfiguration`.

{{% alert title="This explorer does not call anything" color="info" %}}
The page has no server behind it, so **Try it out** and **Authorize** are both gone — a token entered
here could only be attached to a request, and no request can be sent. The padlocks stay, because
which endpoints need one is worth knowing. To call the endpoints, run the API and use its own Swagger
UI at `/swagger` — same document, with **Authorize** and **Try it out** working — or the
[`.http` files or Bruno collection](/heimdall-api/docs/getting-started/#calling-the-api) in
`api-client/`. For what each endpoint means and who may call it in prose, see the
[API reference](../api-reference/).
{{% /alert %}}

<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5.32.13/swagger-ui.css" integrity="sha384-tRpWwikYYdk1+1Mu0osh0Tz/Ay5xgS+s/Nf2Aa7GVAFtZLFdJlAbozfrq4g+xHBK" crossorigin="anonymous">

<div id="swagger-ui" class="heimdall-swagger"></div>

<script src="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5.32.13/swagger-ui-bundle.js" integrity="sha384-PsJla434CobCNv3y1K4wRavOqkUAvwGEQEfbUmI98CCqqGCJsmuDsgIjM6ZQQODP" crossorigin="anonymous"></script>
<script>
  // Width and dark mode are handled in assets/scss/_styles_project.scss, against .heimdall-swagger.
  window.addEventListener('load', function () {
    window.SwaggerUIBundle({
      url: '/heimdall-api/openapi/heimdall.json',
      dom_id: '#swagger-ui',
      // No server is reachable from this page, so an enabled "Try it out" would only ever produce a
      // failed fetch. The API's own Swagger UI is where requests get sent.
      supportedSubmitMethods: [],
      docExpansion: 'none',
      defaultModelsExpandDepth: 0,
      deepLinking: true,
      filter: true,
      tryItOutEnabled: false,
      presets: [window.SwaggerUIBundle.presets.apis],
      // Authorizing has nothing to authorize against here: a token entered on this page could only
      // be attached to a request, and no request can be sent. The button is removed rather than
      // hidden, so it cannot be reached by keyboard either. The per-operation padlocks stay — they
      // are what says an endpoint needs a token, which is worth reading even when it cannot be
      // supplied — and are made inert in assets/scss/_styles_project.scss so they do not open the
      // same dead dialog.
      plugins: [{ wrapComponents: { authorizeBtn: function () { return function () { return null; }; } } }],
      layout: 'BaseLayout'
    });
  });
</script>
