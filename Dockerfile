FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

ARG TARGETARCH

COPY . /source

WORKDIR /source

RUN --mount=type=cache,id=nuget-${TARGETARCH},target=/root/.nuget/packages \
    dotnet publish -a ${TARGETARCH/amd64/x64} --use-current-runtime --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

RUN apk add --no-cache tzdata

WORKDIR /app

COPY --from=build /app .

EXPOSE 5080

ENTRYPOINT ["dotnet", "MobySync.dll"]