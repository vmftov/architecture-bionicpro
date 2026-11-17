FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

COPY ["reports-api.csproj", "./"]
RUN dotnet restore "reports-api.csproj"

COPY . .
RUN dotnet publish "reports-api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:3002
EXPOSE 3002

ENTRYPOINT ["dotnet", "reports-api.dll"]
