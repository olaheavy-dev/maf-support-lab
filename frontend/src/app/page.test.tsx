import { render, screen } from "@testing-library/react";
import Home from "./page";

describe("Home", () => {
  it("has one main heading", () => {
    render(<Home />);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("It works.");
  });
});
