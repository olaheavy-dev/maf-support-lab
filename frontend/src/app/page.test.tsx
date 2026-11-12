import { render, screen } from "@testing-library/react";
import Home from "./page";

describe("Home", () => {
  it("greets the customer with one main heading", () => {
    render(<Home />);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("How can we help, Ada?");
  });

  it("offers a prompt for each scenario group, including the guardrails", () => {
    render(<Home />);
    for (const group of ["Orders", "Billing", "Technical", "Test the guardrails"]) {
      expect(screen.getByRole("heading", { level: 2, name: group })).toBeInTheDocument();
    }
    expect(screen.getByRole("button", { name: "What's the status of ORD-2001?" })).toBeEnabled();
  });

  it("has a labelled message box and a send button that waits for text", () => {
    render(<Home />);
    expect(screen.getByLabelText("Message VoltCart support")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
  });
});
