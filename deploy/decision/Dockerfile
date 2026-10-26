FROM ghcr.io/astral-sh/uv:0.9.6 AS uv
FROM python:3.13-slim-bookworm
COPY --from=uv /uv /usr/local/bin/uv
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /opt/kev
RUN git init && git remote add origin https://github.com/jaredpalmer/kev.git \
 && git fetch --depth 1 origin 6b719c3c3f367295f6ef336f4f751cf5ff970abc \
 && git checkout --detach FETCH_HEAD && test "$(git rev-parse HEAD)" = 6b719c3c3f367295f6ef336f4f751cf5ff970abc \
 && uv sync --frozen --extra serve --no-dev
COPY deploy/decision/harden.py /opt/decision/harden.py
RUN python /opt/decision/harden.py
COPY deploy/decision/download.py /opt/decision/download.py
COPY deploy/decision/start.sh /opt/decision/start.sh
ENV HF_HOME=/cache/huggingface HF_HUB_OFFLINE=1 KEV_PREFIX_CACHE=0 KEV_TRUNCATE_STATES=0 UV_NO_SYNC=1
RUN useradd --uid 1654 --create-home decision && mkdir /cache && chown 1654:1654 /cache
USER 1654:1654
EXPOSE 8009
ENTRYPOINT ["sh", "/opt/decision/start.sh"]
