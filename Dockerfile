# Use official .NET SDK image to build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy everything and restore
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o out

# Final image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .

# Expose port (your HTTPS port)
EXPOSE 80

ENTRYPOINT ["dotnet", "iPortfolioBackend.dll"]
