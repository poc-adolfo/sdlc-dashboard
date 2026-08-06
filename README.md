# sdlc-dashboard

Frontend operacional do pipeline SDLC Hermes. Ver spec em `sdlc-agentico/specs/frontend-operacional-sdlc-hermes.md`.

Stack: React (mobile-first) + .NET 8.


## Production authentication secrets

The API has no weak defaults: `Authentication:Username`, `Authentication:Password`, and `Authentication:SigningKey` are required at startup. The application validates that the signing key decodes to exactly 32 bytes (256 bits), but this check cannot prove randomness or entropy. The secret-generation process must provide that guarantee: generate it with the operating system CSPRNG using `openssl rand -base64 32`, and store it in a secret manager. In Kubernetes, inject these values from a namespace-scoped Secret using the standard .NET environment-variable mapping (`Authentication__Username`, `Authentication__Password`, and `Authentication__SigningKey`). Do not put values in a Deployment or ConfigMap.

Example (replace the placeholders through your secret-management pipeline; do not commit real values):

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: sdlc-dashboard-auth
  namespace: sdlc
type: Opaque
stringData:
  username: <operator-username>
  password: <generated-password>
  signingKey: <output-of-openssl-rand--base64-32>
---
# Add to the API Deployment container:
env:
  - name: Authentication__Username
    valueFrom: { secretKeyRef: { name: sdlc-dashboard-auth, key: username } }
  - name: Authentication__Password
    valueFrom: { secretKeyRef: { name: sdlc-dashboard-auth, key: password } }
  - name: Authentication__SigningKey
    valueFrom: { secretKeyRef: { name: sdlc-dashboard-auth, key: signingKey } }
```

The current scope intentionally remains one operator login: sections 7 and 11 of `frontend-operacional-sdlc-hermes.md` leave multitenant isolation and multi-user authorization out of this WBS; resource authorization is therefore not implemented in this correction.
