FROM mcr.microsoft.com/devcontainers/dotnet:2-10.0-noble

USER root

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        fish \
        postgresql-client \
        protobuf-compiler \
    && rm -rf /var/lib/apt/lists/*

# Pin the installer and the release so a rebuild does not silently change the prompt.
RUN curl -fsSL https://raw.githubusercontent.com/starship/starship/v1.26.0/install/install.sh \
        -o /tmp/starship-install.sh \
    && sh /tmp/starship-install.sh --yes --bin-dir /usr/local/bin --version v1.26.0 \
    && rm /tmp/starship-install.sh

RUN dotnet tool install --tool-path /usr/local/bin dotnet-ef --version 10.0.0

COPY config.fish /home/vscode/.config/fish/config.fish
RUN chown -R vscode:vscode /home/vscode/.config/fish \
    && usermod --shell /usr/bin/fish vscode

USER vscode
