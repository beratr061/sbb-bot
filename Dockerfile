# Base image for build
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY ["sbb-bot/sbb-bot.csproj", "sbb-bot/"]
RUN dotnet restore "sbb-bot/sbb-bot.csproj"

# Copy the rest of the source code
COPY . .

# Publish the application
WORKDIR "/src/sbb-bot"
RUN dotnet publish -c Release -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0-preview
WORKDIR /app
COPY --from=build /app/publish .

# Data directory for JSON storage
# Note: In Railway (without volumes), this data is ephemeral and resets on deploy/restart.
# The application handles "First Run" scenarios gracefully, so this is acceptable for basic usage.

ENTRYPOINT ["dotnet", "sbb-bot.dll"]
