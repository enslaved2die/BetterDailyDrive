# Fetches the pre-built self-contained binary from a GitHub Release instead of compiling from
# source, so building this image needs no .NET SDK and no source checkout - just this Dockerfile.
FROM alpine:3.20 AS fetch
# "latest" always resolves to whichever GitHub Release is currently marked Latest, via GitHub's
# releases/latest/download/ redirect - no tag to bump for every new release. Pass a specific tag
# (e.g. --build-arg VERSION=v2.0.0) instead if you want to pin to a fixed version.
ARG VERSION=latest
ARG TARGETARCH
RUN apk add --no-cache curl && \
    case "$TARGETARCH" in \
      amd64) ASSET=BetterDailyDrive-linux-x64 ;; \
      arm64) ASSET=BetterDailyDrive-linux-arm64 ;; \
      *) echo "Unsupported architecture: $TARGETARCH" >&2 && exit 1 ;; \
    esac && \
    if [ "$VERSION" = "latest" ]; then \
      URL="https://github.com/enslaved2die/BetterDailyDrive/releases/latest/download/${ASSET}"; \
    else \
      URL="https://github.com/enslaved2die/BetterDailyDrive/releases/download/${VERSION}/${ASSET}"; \
    fi && \
    curl -fSL "$URL" -o /BetterDailyDrive && \
    chmod +x /BetterDailyDrive

# runtime-deps has just the native OS libraries .NET needs (openssl, icu, etc.), not the .NET
# runtime itself - the self-contained binary already bundles that, so this stays lean. It's the
# same glibc-based Debian image the linux-x64/linux-arm64 (not musl) release binaries are built for.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
COPY --from=fetch /BetterDailyDrive /app/BetterDailyDrive
WORKDIR /data
EXPOSE 5080
# The OAuth callback port - only reachable *from a browser* (not container-to-container), so it
# needs publishing too whenever CALLBACK_HOST is set to something other than 127.0.0.1. See
# AuthManager's CallbackUri comment and the README's Docker section for why/how.
EXPOSE 58739
ENTRYPOINT ["/app/BetterDailyDrive", "--ui"]
