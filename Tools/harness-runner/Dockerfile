# AgOpenWeb
# Copyright (C) 2026 AgOpenWeb Contributors
# SPDX-License-Identifier: Apache-2.0
# AgOpenWeb scenario harness runner: .NET as CI pins it, Playwright's Chromium for the web client
# and imperial harnesses. Each container has its own network, so harnesses that use UDP 9999/8888
# on the loopback run side by side.
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates libicu74 git xz-utils util-linux \
    && rm -rf /var/lib/apt/lists/*
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/i.sh \
    && bash /tmp/i.sh --version 10.0.401 --install-dir /opt/dotnet && rm /tmp/i.sh
# Node 22 (Playwright 1.64 needs 20 or later; Ubuntu 24.04 packages 18), for its installer only.
RUN arch=$(dpkg --print-architecture | sed 's/amd64/x64/') \
    && curl -sSL https://nodejs.org/dist/v22.12.0/node-v22.12.0-linux-$arch.tar.xz | tar -xJ -C /opt \
    && ln -s /opt/node-v22.12.0-linux-$arch/bin/node /usr/local/bin/node \
    && ln -s /opt/node-v22.12.0-linux-$arch/bin/npx /usr/local/bin/npx \
    && ln -s /opt/node-v22.12.0-linux-$arch/bin/npm /usr/local/bin/npm
# The Chromium revision the tests' Microsoft.Playwright 1.64.1 expects, with its system libraries.
RUN npx -y playwright@1.64.1 install --with-deps chromium && rm -rf /root/.npm /var/lib/apt/lists/*
COPY run-harness.sh /usr/local/bin/run-harness
# A normal user, as on CI: a test that takes a folder's permissions away (a file that can't be
# removed) does nothing under root.
RUN useradd -m runner && mkdir -p /work /out /cache && chown runner /work /out /cache && chmod -R a+rX /ms-playwright
USER runner
RUN git config --global --add safe.directory '*'
WORKDIR /work
ENTRYPOINT ["run-harness"]
