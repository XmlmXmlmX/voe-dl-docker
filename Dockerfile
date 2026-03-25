FROM python:3.11-slim

WORKDIR /app

# Install dependencies first for better layer caching
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy application source
COPY . .

# Directory for downloaded files (mount a host volume here)
RUN mkdir -p /downloads
ENV DOWNLOAD_DIR=/downloads

# Bake the app version into the image at build time.
# Pass --build-arg APP_VERSION=<tag> when building, e.g. via CI.
ARG APP_VERSION=dev
ENV APP_VERSION=${APP_VERSION}

EXPOSE 5000

CMD ["python", "app.py"]
