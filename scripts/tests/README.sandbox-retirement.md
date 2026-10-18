# Pinned server retirement HTTP proof

Run the private retirement endpoint/authentication/provider-hook proof without a
Docker socket, network, host ports or mutable user data:

```sh
docker run --rm --pull never --network none --read-only \
  --tmpfs /var/lib/opensandbox:rw,nosuid,nodev,noexec,size=8m \
  --tmpfs /tmp:rw,nosuid,nodev,noexec,size=8m \
  --env PYTHONDONTWRITEBYTECODE=1 \
  --mount type=bind,src="$PWD/deploy/sandbox/retirement.py",dst=/proof/retirement.py,readonly \
  --mount type=bind,src="$PWD/scripts/tests/opensandbox_retirement_http_proof.py",dst=/proof/proof.py,readonly \
  --entrypoint python \
  opensandbox/server@sha256:bcd5f264101262cebfa7e1ff9cd0424403e42f31a6596e4364cf90d8cb5c936b \
  /proof/proof.py
```

The installed pinned vendor classes, FastAPI, Pydantic and authentication middleware
are real. Docker provisioning/deletion is a controlled peer. This does not qualify
an actual Docker bind, user command, daemon outage or production tenant isolation.
Existing running servers are not restarted by this command.
