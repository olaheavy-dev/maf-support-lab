import { fireEvent, render, screen } from "@testing-library/react";

import { ErrorMessage } from "./error-message";

describe("the error page", () => {
  it("shows the server's reference, never the error's message", () => {
    const error = Object.assign(new Error("password=hunter2"), { digest: "1234567" });
    render(<ErrorMessage error={error} retry={() => {}} />);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent("Something went wrong");
    expect(screen.getByText("1234567")).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("hunter2");
  });

  it("copes with a thrown value that isn't an Error", () => {
    render(<ErrorMessage error="just a string" retry={() => {}} />);
    expect(screen.queryByText(/Reference/)).not.toBeInTheDocument();
  });

  it("tries again when asked", () => {
    const retry = vi.fn();
    render(<ErrorMessage error={new Error("x")} retry={retry} />);
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(retry).toHaveBeenCalledOnce();
  });
});
