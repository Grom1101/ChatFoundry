FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["tests/Mocks/TelegramMock/TelegramMock.csproj", "tests/Mocks/TelegramMock/"]
RUN dotnet restore "tests/Mocks/TelegramMock/TelegramMock.csproj"
COPY ["tests/Mocks/TelegramMock/", "tests/Mocks/TelegramMock/"]
WORKDIR "/src/tests/Mocks/TelegramMock"
RUN dotnet build "TelegramMock.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "TelegramMock.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TelegramMock.dll"]
