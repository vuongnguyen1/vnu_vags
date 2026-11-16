FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["VagsBackend.csproj", "./"]
RUN dotnet restore "VagsBackend.csproj"
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-openpyxl && rm -rf /var/lib/apt/lists/*
EXPOSE 8080 8443
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "VagsBackend.dll"]

