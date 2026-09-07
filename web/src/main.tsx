import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import { App } from "./App"
import { LandingPage } from "./LandingPage"
import "./styles.css"
import "./landing.css"

const showApplication = window.location.pathname === "/app"

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {showApplication ? <App /> : <LandingPage />}
  </StrictMode>,
)
