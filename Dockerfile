FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

ARG TARGETARCH

COPY . /source

WORKDIR /source

RUN --mount=type=cache,id=nuget-${TARGETARCH},target=/root/.nuget/packages \
    dotnet publish MobySync.csproj -a ${TARGETARCH/amd64/x64} --use-current-runtime --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

RUN apk add --no-cache tzdata

WORKDIR /app

COPY --from=build /app .
COPY --from=build /source/appsettings-example.json ./appsettings.json

EXPOSE 5080

HEALTHCHECK --interval=5s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:5080/health || exit 1

ENTRYPOINT ["dotnet", "MobySync.dll"]