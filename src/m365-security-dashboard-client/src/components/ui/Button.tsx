import React from "react";

export type ButtonVariant = "primary" | "secondary" | "tertiary";

/** Themed button. Purely presentational — pass the real handler through. */
export function Button({ variant = "secondary", className, type = "button", children, ...rest }: {
  variant?: ButtonVariant;
} & React.ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button type={type} className={`ui-btn ui-btn-${variant}${className ? ` ${className}` : ""}`} {...rest}>
      {children}
    </button>
  );
}
