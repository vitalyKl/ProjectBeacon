# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-bookworm-slim AS build
WORKDIR /src
COPY global.json ./
COPY ProjectBeacon.sln ./
COPY ProjectBeacon.Domain/ProjectBeacon.Domain.csproj ProjectBeacon.Domain/
COPY ProjectBeacon.Infrastructure/ProjectBeacon.Infrastructure.csproj ProjectBeacon.Infrastructure/
COPY ProjectBeacon.Application/ProjectBeacon.Application.csproj ProjectBeacon.Application/
COPY ProjectBeacon.API/ProjectBeacon.API.csproj ProjectBeacon.API/
COPY ProjectBeacon.Web/ProjectBeacon.Web.csproj ProjectBeacon.Web/
RUN dotnet restore ProjectBeacon.Web/ProjectBeacon.Web.csproj
COPY . .
ARG BEACON_GIT_SHA=unknown
ARG BEACON_VERSION=0.1.0
RUN dotnet publish ProjectBeacon.Web/ProjectBeacon.Web.csproj -c Release -o /app --no-restore \
    -p:Version=${BEACON_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /app .
ARG BEACON_GIT_SHA=unknown
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV BEACON_MIGRATE_ON_START=false
ENV BEACON_GIT_SHA=${BEACON_GIT_SHA}
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ProjectBeacon.Web.dll"]
