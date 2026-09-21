from pathlib import Path
from pydantic_settings import BaseSettings, SettingsConfigDict

_BASE_DIR = Path(__file__).resolve().parents[2]
_ROOT_DIR = Path(__file__).resolve().parents[3]

class Settings(BaseSettings):
    ENVIRONMENT: str = "development"
    DEBUG: bool = True
    HOST: str = "0.0.0.0"
    PORT: int = 8000
    SECRET_KEY: str = "insecure_dev_secret_key_change_in_production"
    POSTGRES_HOST: str = "5.189.170.180"
    POSTGRES_PORT: int = 5432
    POSTGRES_DB: str = "enightx_pos_dev"
    POSTGRES_USER: str = "enightx_dev"
    POSTGRES_PASSWORD: str = "hfVVWfhJG2L17NF-K5mse5XHeUQVbSNT"

    model_config = SettingsConfigDict(
        env_file=(
            str(_BASE_DIR / ".env"),
            str(_ROOT_DIR / ".env"),
            ".env",
            "apps/api/.env"
        ),
        env_file_encoding="utf-8",
        extra="ignore"
    )

settings = Settings()
