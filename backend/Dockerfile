# PayTaxi API — multi-stage build. Build context = the `backend/` folder.
#   docker build -t paytaxi-api ./backend
#   docker run -p 8080:8080 --env-file .env paytaxi-api
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first for layer caching.
COPY PayTaxi.sln ./
COPY PayTaxi.Core/PayTaxi.Core.csproj PayTaxi.Core/
COPY PayTaxi.Infrastructure/PayTaxi.Infrastructure.csproj PayTaxi.Infrastructure/
COPY PayTaxi.Api/PayTaxi.Api.csproj PayTaxi.Api/
RUN dotnet restore PayTaxi.Api/PayTaxi.Api.csproj

COPY . .
RUN dotnet publish PayTaxi.Api/PayTaxi.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
# QuestPDF needs fontconfig + a font with Georgian glyphs for the invoice PDFs.
RUN apt-get update && apt-get install -y --no-install-recommends fontconfig fonts-noto-core && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "PayTaxi.Api.dll"]
